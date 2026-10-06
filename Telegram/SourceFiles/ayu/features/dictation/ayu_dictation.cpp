// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#include "ayu/features/dictation/ayu_dictation.h"

#include "api/api_compose_with_ai.h"
#include "apiwrap.h"
#include "ayu/ayu_settings.h"
#include "base/event_filter.h"
#include "core/application.h"
#include "core/core_settings.h"
#include "core/shortcuts.h"
#include "lang_auto.h"
#include "main/main_session.h"
#include "media/audio/media_audio_capture.h"
#include "media/audio/media_audio_capture_common.h"
#include "ui/widgets/fields/input_field.h"
#include "window/window_controller.h"
#include "window/window_session_controller.h"

#include <QtCore/QJsonDocument>
#include <QtCore/QJsonObject>
#include <QtCore/QPointer>
#include <QtCore/QTimer>
#include <QtGui/QClipboard>
#include <QtGui/QGuiApplication>
#include <QtNetwork/QHttpMultiPart>
#include <QtNetwork/QNetworkAccessManager>
#include <QtNetwork/QNetworkReply>
#include <QtNetwork/QNetworkRequest>
#include <QtWidgets/QApplication>
#include <QtWidgets/QTextEdit>

namespace Ayu::Dictation {
namespace {

constexpr auto kTranscribeUrl = "https://api.openai.com/v1/audio/transcriptions";
constexpr auto kTranscribeModel = "gpt-4o-transcribe";
constexpr auto kMaxRecordDuration = 10 * 60 * crl::time(1000);
constexpr auto kRequestTimeout = 120 * crl::time(1000);

const auto kDictatedProperty = "ayuDictated";
const auto kPolishingProperty = "ayuPolishing";
const auto kFilterProperty = "ayuDictationFilter";

struct State {
	QPointer<Ui::InputField> target;
	bool recording = false;
	bool transcribing = false;
	int recordId = 0;
	rpl::lifetime recordLifetime;
	rpl::lifetime lifetime;
	QNetworkAccessManager *nam = nullptr;
};

State &GetState() {
	static auto result = State();
	return result;
}

[[nodiscard]] Window::Controller *WindowFor(QWidget *widget) {
	if (widget) {
		if (const auto window = Core::App().findWindow(widget)) {
			return window;
		}
	}
	return Core::App().activeWindow();
}

void ShowToast(QWidget *widget, const QString &text) {
	if (const auto window = WindowFor(widget)) {
		window->showToast(text);
	}
}

[[nodiscard]] Ui::InputField *FocusedField() {
	for (auto widget = QApplication::focusWidget()
		; widget
		; widget = widget->parentWidget()) {
		if (const auto field = dynamic_cast<Ui::InputField*>(widget)) {
			return field;
		}
	}
	return nullptr;
}

[[nodiscard]] QString PolishPrompt() {
	const auto language = AyuSettings::getInstance().aiPolishLanguage();
	const auto target = (language == u"ru"_q)
		? u"Russian"_q
		: (language == u"en"_q)
		? u"English"_q
		: u"the language most of the text is in"_q;
	return u"The text was dictated by voice and may mix Russian and "
		"English words in one sentence. Rewrite it as one clean, natural "
		"message written entirely in %1. Fix speech recognition mistakes, "
		"grammar and punctuation, and make it sound articulate and a bit "
		"smarter, while keeping the meaning, the tone and roughly the "
		"same length. Do not add facts, greetings or comments."_q.arg(target);
}

void Polish(not_null<Ui::InputField*> field) {
	const auto window = WindowFor(field);
	const auto controller = window ? window->sessionController() : nullptr;
	if (!controller) {
		field->setProperty(kDictatedProperty, false);
		return;
	}
	const auto &current = field->getTextWithTags();
	auto text = TextWithEntities{
		current.text,
		TextUtilities::ConvertTextTagsToEntities(current.tags),
	};
	if (text.text.trimmed().isEmpty()) {
		field->setProperty(kDictatedProperty, false);
		return;
	}
	field->setProperty(kPolishingProperty, true);
	ShowToast(field, tr::ayu_DictationPolishing(tr::now));

	const auto weak = QPointer<Ui::InputField>(field.get());
	const auto original = current;
	const auto language = AyuSettings::getInstance().aiPolishLanguage();
	auto request = Api::ComposeWithAi::Request{
		.text = std::move(text),
		.translateToLang = language,
		.tone = Api::ComposeWithAi::ToneRef{
			.customPrompt = PolishPrompt(),
		},
	};
	(void)controller->session().api().composeWithAi().request(
		std::move(request),
		[=](Api::ComposeWithAi::Result &&result) {
			if (!weak) {
				return;
			}
			weak->setProperty(kPolishingProperty, false);
			weak->setProperty(kDictatedProperty, false);
			if (weak->getTextWithTags() != original
				|| result.resultText.text.trimmed().isEmpty()) {
				// The user edited the text while we were waiting.
				return;
			}
			weak->setTextWithTags({
				result.resultText.text,
				TextUtilities::ConvertEntitiesToTextTags(
					result.resultText.entities),
			}, Ui::InputField::HistoryAction::NewEntry);
			auto cursor = weak->textCursor();
			cursor.movePosition(QTextCursor::End);
			weak->setTextCursor(cursor);
		},
		[=](const MTP::Error &error) {
			if (!weak) {
				return;
			}
			weak->setProperty(kPolishingProperty, false);
			weak->setProperty(kDictatedProperty, false);
			ShowToast(
				weak,
				tr::ayu_DictationPolishFailed(tr::now)
					+ ' '
					+ error.type());
		});
}

void InstallPolishFilter(not_null<Ui::InputField*> field) {
	if (field->property(kFilterProperty).toBool()) {
		return;
	}
	field->setProperty(kFilterProperty, true);

	const auto raw = field.get();
	field->changes() | rpl::on_next([=] {
		if (raw->getLastText().isEmpty()) {
			raw->setProperty(kDictatedProperty, false);
		}
	}, field->lifetime());

	base::install_event_filter(raw, raw->rawTextEdit(), [=](
			not_null<QEvent*> e) {
		if (e->type() != QEvent::KeyPress) {
			return base::EventFilterResult::Continue;
		}
		const auto event = static_cast<QKeyEvent*>(e.get());
		const auto key = event->key();
		if (key != Qt::Key_Enter && key != Qt::Key_Return) {
			return base::EventFilterResult::Continue;
		}
		if (raw->property(kPolishingProperty).toBool()) {
			// Do not send the raw text while the AI is working.
			return base::EventFilterResult::Cancel;
		}
		if (!raw->property(kDictatedProperty).toBool()
			|| !AyuSettings::getInstance().aiPolishOnEnter()
			|| !Ui::InputField::ShouldSubmit(
				Core::App().settings().sendSubmitWay(),
				event->modifiers())) {
			return base::EventFilterResult::Continue;
		}
		Polish(raw);
		return base::EventFilterResult::Cancel;
	});
}

void InsertText(not_null<Ui::InputField*> field, QString text) {
	auto cursor = field->textCursor();
	const auto position = cursor.position();
	if (position > 0) {
		const auto &last = field->getLastText();
		const auto index = std::min(position, int(last.size())) - 1;
		if (index >= 0 && !last.at(index).isSpace()) {
			text = ' ' + text;
		}
	}
	cursor.insertText(text);
	field->setTextCursor(cursor);
	field->setProperty(kDictatedProperty, true);
	InstallPolishFilter(field);
	field->setFocus();
}

[[nodiscard]] QString ParseError(const QByteArray &body, QNetworkReply *reply) {
	const auto document = QJsonDocument::fromJson(body);
	const auto message = document.object()
		.value(u"error"_q).toObject()
		.value(u"message"_q).toString();
	return message.isEmpty() ? reply->errorString() : message;
}

void Transcribe(QByteArray bytes) {
	auto &state = GetState();
	const auto key = apiKey();
	if (key.isEmpty()) {
		ShowToast(state.target, tr::ayu_DictationNoKey(tr::now));
		return;
	}
	if (!state.nam) {
		state.nam = new QNetworkAccessManager(qApp);
	}
	state.transcribing = true;
	ShowToast(state.target, tr::ayu_DictationTranscribing(tr::now));

	const auto multipart = new QHttpMultiPart(QHttpMultiPart::FormDataType);
	const auto addField = [&](const QString &name, const QString &value) {
		auto part = QHttpPart();
		part.setHeader(
			QNetworkRequest::ContentDispositionHeader,
			u"form-data; name=\"%1\""_q.arg(name));
		part.setBody(value.toUtf8());
		multipart->append(part);
	};
	addField(u"model"_q, QString::fromLatin1(kTranscribeModel));
	addField(u"response_format"_q, u"json"_q);
	addField(
		u"prompt"_q,
		u"Разговорная речь, русский вперемешку с English words, "
		"например: давай сделаем deploy и проверим логи."_q);

	auto file = QHttpPart();
	file.setHeader(
		QNetworkRequest::ContentDispositionHeader,
		u"form-data; name=\"file\"; filename=\"dictation.ogg\""_q);
	file.setHeader(QNetworkRequest::ContentTypeHeader, u"audio/ogg"_q);
	file.setBody(bytes);
	multipart->append(file);

	auto request = QNetworkRequest(QUrl(QString::fromLatin1(kTranscribeUrl)));
	request.setRawHeader("Authorization", "Bearer " + key.toUtf8());
	request.setTransferTimeout(int(kRequestTimeout));

	const auto reply = state.nam->post(request, multipart);
	multipart->setParent(reply);

	QObject::connect(reply, &QNetworkReply::finished, [=] {
		auto &now = GetState();
		now.transcribing = false;
		reply->deleteLater();

		const auto body = reply->readAll();
		if (reply->error() != QNetworkReply::NoError) {
			ShowToast(
				now.target,
				tr::ayu_DictationFailed(tr::now)
					+ ' '
					+ ParseError(body, reply));
			return;
		}
		const auto text = QJsonDocument::fromJson(body).object()
			.value(u"text"_q).toString().trimmed();
		if (text.isEmpty()) {
			ShowToast(now.target, tr::ayu_DictationEmpty(tr::now));
			return;
		}
		if (const auto field = now.target.data()) {
			InsertText(field, text);
		} else if (const auto focused = FocusedField()) {
			InsertText(focused, text);
		} else {
			QGuiApplication::clipboard()->setText(text);
		}
	});
}

void Stop() {
	auto &state = GetState();
	if (!state.recording) {
		return;
	}
	state.recording = false;
	state.recordLifetime.destroy();
	::Media::Capture::instance()->stop([=](::Media::Capture::Result &&result) {
		if (result.bytes.isEmpty()) {
			ShowToast(GetState().target, tr::ayu_DictationEmpty(tr::now));
			return;
		}
		Transcribe(std::move(result.bytes));
	});
}

void Start() {
	auto &state = GetState();
	const auto field = FocusedField();
	if (!field) {
		ShowToast(nullptr, tr::ayu_DictationNoField(tr::now));
		return;
	}
	if (apiKey().isEmpty()) {
		ShowToast(field, tr::ayu_DictationNoKey(tr::now));
		return;
	}
	const auto capture = ::Media::Capture::instance();
	if (!capture) {
		ShowToast(field, tr::ayu_DictationNoMic(tr::now));
		return;
	}
	if (capture->started() || state.transcribing) {
		ShowToast(field, tr::ayu_DictationBusy(tr::now));
		return;
	}
	capture->check();
	if (!capture->available()) {
		ShowToast(field, tr::ayu_DictationNoMic(tr::now));
		return;
	}
	state.target = field;
	state.recording = true;
	const auto id = ++state.recordId;

	capture->start();
	capture->updated(
	) | rpl::on_error([=](::Media::Capture::Error) {
		auto &now = GetState();
		if (now.recording && now.recordId == id) {
			now.recording = false;
			now.recordLifetime.destroy();
			ShowToast(now.target, tr::ayu_DictationNoMic(tr::now));
		}
	}, state.recordLifetime);

	QTimer::singleShot(int(kMaxRecordDuration), [=] {
		const auto &now = GetState();
		if (now.recording && now.recordId == id) {
			Stop();
		}
	});
	ShowToast(field, tr::ayu_DictationListening(tr::now));
}

void Toggle() {
	if (GetState().recording) {
		Stop();
	} else {
		Start();
	}
}

} // namespace

QString apiKey() {
	const auto custom = AyuSettings::getInstance().aiDictationKey().trimmed();
	if (!custom.isEmpty()) {
		return custom;
	}
#ifdef AYU_OPENAI_API_KEY
	return QString::fromLatin1(AYU_OPENAI_API_KEY).trimmed();
#else // AYU_OPENAI_API_KEY
	return QString();
#endif // AYU_OPENAI_API_KEY
}

bool hasBuiltInKey() {
#ifdef AYU_OPENAI_API_KEY
	return !QString::fromLatin1(AYU_OPENAI_API_KEY).trimmed().isEmpty();
#else // AYU_OPENAI_API_KEY
	return false;
#endif // AYU_OPENAI_API_KEY
}

void init() {
	Shortcuts::Requests(
	) | rpl::on_next([=](not_null<Shortcuts::Request*> request) {
		using Command = Shortcuts::Command;
		request->check(Command::AyuDictate, 1) && request->handle([=] {
			Toggle();
			return true;
		});
	}, GetState().lifetime);
}

} // namespace Ayu::Dictation
