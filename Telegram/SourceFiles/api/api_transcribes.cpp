/*
This file is part of Telegram Desktop,
the official desktop application for the Telegram messaging service.

For license and copyright information please follow this link:
https://github.com/telegramdesktop/tdesktop/blob/master/LEGAL
*/
#include "api/api_transcribes.h"

#include "apiwrap.h"
#include "api/api_text_entities.h"
#include "data/data_channel.h"
#include "data/data_document.h"
#include "data/data_peer.h"
#include "data/data_session.h"
#include "history/history.h"
#include "history/history_item.h"
#include "history/history_item_helpers.h"
#include "lang/lang_keys.h"
#include "main/main_app_config.h"
#include "main/main_session.h"
#include "main/main_session_settings.h"
#include "spellcheck/spellcheck_types.h"

// AyuGram includes
#include "ayu/features/voice_transcribe/voice_transcribe.h"
#include "data/data_document_media.h"
#include "data/data_file_origin.h"
#include "data/data_media_types.h"

#include <QtCore/QFile>

namespace Api {
namespace {

namespace Voice = AyuFeatures::VoiceTranscribe;

// Not a real MTP request: marks "own Whisper request in flight", so the
// existing UI shows the loading state and toggle() waits for the answer.
constexpr auto kLocalRequestId = mtpRequestId(-1);

[[nodiscard]] DocumentData *VoiceDocument(not_null<HistoryItem*> item) {
	const auto media = item->media();
	const auto document = media ? media->document() : nullptr;
	return (document
		&& !media->ttlSeconds()
		&& (document->isVoiceMessage() || document->isVideoMessage()))
		? document
		: nullptr;
}

[[nodiscard]] QByteArray ReadVoiceBytes(
		const std::shared_ptr<Data::DocumentMedia> &media) {
	auto result = media->bytes();
	if (result.isEmpty()) {
		const auto path = media->owner()->filepath(true);
		if (!path.isEmpty()) {
			auto file = QFile(path);
			if (file.open(QIODevice::ReadOnly)) {
				result = file.readAll();
			}
		}
	}
	return result;
}

[[nodiscard]] QString VoiceFileName(not_null<DocumentData*> document) {
	const auto mime = document->mimeString().toLower();
	return document->isVideoMessage()
		? u"round.mp4"_q
		: mime.contains(u"mpeg"_q)
		? u"voice.mp3"_q
		: (mime.contains(u"mp4"_q) || mime.contains(u"m4a"_q))
		? u"voice.m4a"_q
		: mime.contains(u"wav"_q)
		? u"voice.wav"_q
		: u"voice.ogg"_q;
}

} // namespace

Transcribes::Transcribes(not_null<ApiWrap*> api)
: _session(&api->session())
, _api(&api->instance()) {
	// Reads the config now, so log.txt tells right away whether own
	// transcription is on (AYU_VOICE.cmd checks it after install).
	[[maybe_unused]] const auto available = Voice::Available();
}

bool Transcribes::isRated(not_null<HistoryItem*> item) const {
	const auto fullId = item->fullId();
	for (const auto &[transcribeId, id] : _ids) {
		if (id == fullId) {
			return _session->settings().isTranscriptionRated(transcribeId);
		}
	}
	return false;
}

void Transcribes::rate(not_null<HistoryItem*> item, bool isGood) {
	const auto fullId = item->fullId();
	for (const auto &[transcribeId, id] : _ids) {
		if (id == fullId) {
			_api.request(MTPmessages_RateTranscribedAudio(
				item->history()->peer->input(),
				MTP_int(item->id),
				MTP_long(transcribeId),
				MTP_bool(isGood))).send();
			_session->settings().markTranscriptionAsRated(transcribeId);
			_session->saveSettings();
			return;
		}
	}
}

bool Transcribes::freeFor(not_null<HistoryItem*> item) const {
	if (const auto channel = item->history()->peer->asMegagroup()) {
		const auto owner = &channel->owner();
		return channel->levelHint() >= owner->groupFreeTranscribeLevel();
	}
	return false;
}

bool Transcribes::trialsSupport() {
	if (!_trialsSupport) {
		const auto count = _session->appConfig().get<int>(
			u"transcribe_audio_trial_weekly_number"_q,
			0);
		const auto until = _session->appConfig().get<int>(
			u"transcribe_audio_trial_cooldown_until"_q,
			0);
		_trialsSupport = (count > 0) || (until > 0);
	}
	return *_trialsSupport;
}

TimeId Transcribes::trialsRefreshAt() {
	if (_trialsRefreshAt < 0) {
		_trialsRefreshAt = _session->appConfig().get<int>(
			u"transcribe_audio_trial_cooldown_until"_q,
			0);
	}
	return _trialsRefreshAt;
}

int Transcribes::trialsCount() {
	if (_trialsCount < 0) {
		_trialsCount = _session->appConfig().get<int>(
			u"transcribe_audio_trial_weekly_number"_q,
			-1);
		return std::max(_trialsCount, 0);
	}
	return _trialsCount;
}

crl::time Transcribes::trialsMaxLengthMs() const {
	return 1000 * _session->appConfig().get<int>(
		u"transcribe_audio_trial_duration_max"_q,
		300);
}

void Transcribes::toggle(not_null<HistoryItem*> item) {
	const auto id = item->fullId();
	auto i = _map.find(id);
	if (i == _map.end()) {
		if (canLoadLocal(item)) {
			loadLocal(item);
		} else {
			load(item);
		}
		_session->data().requestItemResize(item);
	} else if (i->second.local
		&& i->second.failed
		&& !i->second.toolong
		&& canLoadLocal(item)) {
		// AyuGram: a click on a failed own transcription retries it.
		loadLocal(item);
		_session->data().requestItemResize(item);
	} else if (!i->second.requestId) {
		i->second.shown = !i->second.shown;
		if (i->second.roundview) {
			_session->data().requestItemViewRefresh(item);
		}
		_session->data().requestItemResize(item);
	}
}

void Transcribes::toggleSummary(not_null<HistoryItem*> item) {
	const auto id = item->fullId();
	auto i = _summaries.find(id);
	if (i == _summaries.end()) {
		summarize(item);
	} else if (!i->second.loading) {
		auto &entry = i->second;
		if (entry.result.empty()) {
			summarize(item);
		} else {
			entry.shown = entry.premiumRequired ? false : !entry.shown;
			_session->data().requestItemResize(item);
			if (entry.shown) {
				_session->data().requestItemShowHighlight(item);
			}
		}
	}
}

const Transcribes::Entry &Transcribes::entry(
		not_null<HistoryItem*> item) const {
	static auto empty = Entry();
	const auto i = _map.find(item->fullId());
	return (i != _map.end()) ? i->second : empty;
}

const SummaryEntry &Transcribes::summary(
		not_null<const HistoryItem*> item) const {
	static const auto empty = SummaryEntry();
	const auto i = _summaries.find(item->fullId());
	return (i != _summaries.end()) ? i->second : empty;
}

void Transcribes::apply(const MTPDupdateTranscribedAudio &update) {
	const auto id = update.vtranscription_id().v;
	const auto i = _ids.find(id);
	if (i == _ids.end()) {
		return;
	}
	const auto j = _map.find(i->second);
	if (j == _map.end()) {
		return;
	}
	const auto text = qs(update.vtext());
	j->second.result = text;
	j->second.pending = update.is_pending();
	if (const auto item = _session->data().message(i->second)) {
		if (j->second.roundview) {
			_session->data().requestItemViewRefresh(item);
		}
		_session->data().requestItemResize(item);
	}
}

void Transcribes::load(not_null<HistoryItem*> item) {
	if (!item->isHistoryEntry() || item->isLocal()) {
		return;
	}
	const auto toggleRound = [](not_null<HistoryItem*> item, Entry &entry) {
		if (const auto media = item->media()) {
			if (const auto document = media->document()) {
				if (document->isVideoMessage()) {
					entry.roundview = true;
					document->owner().requestItemViewRefresh(item);
				}
			}
		}
	};
	const auto id = item->fullId();
	const auto requestId = _api.request(MTPmessages_TranscribeAudio(
		item->history()->peer->input(),
		MTP_int(item->id)
	)).done([=](const MTPmessages_TranscribedAudio &result) {
		const auto &data = result.data();

		{
			const auto trialsCountChanged = data.vtrial_remains_num()
				&& (_trialsCount != data.vtrial_remains_num()->v);
			if (trialsCountChanged) {
				_trialsCount = data.vtrial_remains_num()->v;
			}
			const auto refreshAtChanged = data.vtrial_remains_until_date()
				&& (_trialsRefreshAt != data.vtrial_remains_until_date()->v);
			if (refreshAtChanged) {
				_trialsRefreshAt = data.vtrial_remains_until_date()->v;
			}
			if (trialsCountChanged) {
				ShowTrialTranscribesToast(_trialsCount, _trialsRefreshAt);
			}
		}

		auto &entry = _map[id];
		entry.requestId = 0;
		entry.pending = data.is_pending();
		entry.result = qs(data.vtext());
		_ids.emplace(data.vtranscription_id().v, id);
		if (const auto item = _session->data().message(id)) {
			toggleRound(item, entry);
			_session->data().requestItemResize(item);
		}
	}).fail([=](const MTP::Error &error) {
		auto &entry = _map[id];
		entry.requestId = 0;
		entry.pending = false;
		entry.failed = true;
		if (error.type() == u"MSG_VOICE_TOO_LONG"_q) {
			entry.toolong = true;
		}
		if (const auto item = _session->data().message(id)) {
			toggleRound(item, entry);
			_session->data().requestItemResize(item);
		}
	}).send();
	auto &entry = _map.emplace(id).first->second;
	entry.requestId = requestId;
	entry.shown = true;
	entry.failed = false;
	entry.pending = false;
}

void Transcribes::summarize(not_null<HistoryItem*> item) {
	if (!item->isHistoryEntry() || item->isLocal()) {
		return;
	}

	const auto id = item->fullId();
	const auto translatedTo = item->history()->translatedTo();
	const auto langCode = translatedTo
		? translatedTo.twoLetterCode()
		: QString();
	const auto requestId = _api.request(MTPmessages_SummarizeText(
		langCode.isEmpty()
			? MTP_flags(0)
			: MTP_flags(MTPmessages_summarizeText::Flag::f_to_lang),
		item->history()->peer->input(),
		MTP_int(item->id),
		langCode.isEmpty() ? MTPstring() : MTP_string(langCode),
		MTPstring() // tone
	)).done([=](const MTPTextWithEntities &result) {
		const auto &data = result.data();
		auto &entry = _summaries[id];
		entry.requestId = 0;
		entry.loading = false;
		entry.premiumRequired = false;
		entry.languageId = translatedTo;
		entry.result = TextWithEntities(
			qs(data.vtext()),
			Api::EntitiesFromMTP(_session, data.ventities().v));
		if (const auto item = _session->data().message(id)) {
			_session->data().requestItemTextRefresh(item);
			_session->data().requestItemShowHighlight(item);
		}
	}).fail([=](const MTP::Error &error) {
		auto &entry = _summaries[id];
		if (error.type() == u"SUMMARY_FLOOD_PREMIUM"_q) {
			entry.premiumRequired = true;
		}
		entry.requestId = 0;
		entry.shown = false;
		entry.loading = false;
		if (const auto item = _session->data().message(id)) {
			_session->data().requestItemTextRefresh(item);
		}
	}).send();

	auto &entry = _summaries.emplace(id).first->second;
	entry.requestId = requestId;
	entry.shown = true;
	entry.loading = true;

	item->setHasSummaryEntry();
	_session->data().requestItemResize(item);
}

void Transcribes::checkSummaryToTranslate(FullMsgId id) {
	const auto i = _summaries.find(id);
	if (i == _summaries.end() || i->second.result.empty()) {
		return;
	}
	const auto item = _session->data().message(id);
	if (!item) {
		return;
	}
	const auto translatedTo = item->history()->translatedTo();
	if (i->second.languageId != translatedTo) {
		i->second.result = tr::lng_contacts_loading(tr::now, tr::italic);
		summarize(item);
	}
}

bool Transcribes::localAvailable() const {
	return Voice::Available();
}

bool Transcribes::canLoadLocal(not_null<HistoryItem*> item) const {
	return localAvailable()
		&& item->isHistoryEntry()
		&& !item->isLocal()
		&& !item->isScheduled()
		&& VoiceDocument(item);
}

void Transcribes::autoTranscribe(not_null<HistoryItem*> item) {
	if (!Voice::AutoTranscribe()) {
		return;
	}
	const auto id = item->fullId();
	if (_autoRequested.contains(id) || _map.contains(id)) {
		return;
	}
	_autoRequested.insert(id);
	if (!canLoadLocal(item)) {
		return;
	}
	// Called from paint and from new message notifications, so the actual
	// work (and the resize it causes) is postponed out of that stack.
	crl::on_main(this, [=] {
		const auto item = _session->data().message(id);
		if (item && !_map.contains(id) && canLoadLocal(item)) {
			loadLocal(item);
			_session->data().requestItemResize(item);
		}
	});
}

void Transcribes::loadLocal(not_null<HistoryItem*> item) {
	const auto document = VoiceDocument(item);
	if (!document) {
		return;
	}
	const auto id = item->fullId();
	auto &entry = _map.emplace(id).first->second;
	entry.requestId = kLocalRequestId;
	entry.shown = true;
	entry.failed = false;
	entry.toolong = false;
	entry.pending = false;
	entry.local = true;

	auto media = document->createMediaView();
	if (sendLocal(id, media)) {
		return;
	}
	if (_localDownloads.empty()) {
		_session->downloaderTaskFinished(
		) | rpl::on_next([=] {
			checkLocalDownloads();
		}, _localDownloadsLifetime);
	}
	_localDownloads[id] = std::move(media);
	document->save(Data::FileOrigin(id), QString());
	checkLocalDownloads();
}

bool Transcribes::sendLocal(
		FullMsgId id,
		const std::shared_ptr<Data::DocumentMedia> &media) {
	auto bytes = ReadVoiceBytes(media);
	if (bytes.isEmpty()) {
		return false;
	}
	const auto document = media->owner();
	Voice::Transcribe(
		std::move(bytes),
		VoiceFileName(document),
		crl::guard(this, [=](Voice::Result result) {
			finishLocal(id, result);
		}));
	return true;
}

void Transcribes::checkLocalDownloads() {
	auto failed = std::vector<FullMsgId>();
	for (auto i = _localDownloads.begin(); i != _localDownloads.end();) {
		const auto id = i->first;
		const auto media = i->second;
		if (sendLocal(id, media)) {
			i = _localDownloads.erase(i);
		} else if (!media->owner()->loading()) {
			// Download finished without data or was cancelled.
			failed.push_back(id);
			i = _localDownloads.erase(i);
		} else {
			++i;
		}
	}
	if (_localDownloads.empty()) {
		_localDownloadsLifetime.destroy();
	}
	for (const auto &id : failed) {
		finishLocal(id, { .error = Voice::Error::Failed });
	}
}

void Transcribes::finishLocal(FullMsgId id, const Voice::Result &result) {
	const auto i = _map.find(id);
	if (i == _map.end() || i->second.requestId != kLocalRequestId) {
		return;
	}
	const auto item = _session->data().message(id);
	if (result.error == Voice::Error::Failed && item && _session->premium()) {
		// Own key failed, Premium transcription still works.
		_map.erase(i);
		load(item);
		_session->data().requestItemResize(item);
		return;
	}
	auto &entry = i->second;
	entry.requestId = 0;
	entry.pending = false;
	entry.failed = (result.error != Voice::Error::None);
	entry.toolong = (result.error == Voice::Error::TooLong);
	entry.result = entry.failed
		? QString()
		: result.text.isEmpty()
		? QString::fromUtf8("\xF0\x9F\x94\x87") // muted speaker, no speech
		: result.text;
	if (!item) {
		return;
	}
	if (const auto document = VoiceDocument(item)
		; document && document->isVideoMessage()) {
		entry.roundview = true;
		_session->data().requestItemViewRefresh(item);
	}
	_session->data().requestItemResize(item);
}

} // namespace Api
