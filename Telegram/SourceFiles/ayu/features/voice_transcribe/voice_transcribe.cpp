// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#include "ayu/features/voice_transcribe/voice_transcribe.h"

#include "logs.h"
#include "settings.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QFile>
#include <QtCore/QJsonArray>
#include <QtCore/QJsonDocument>
#include <QtCore/QJsonObject>
#include <QtCore/QProcessEnvironment>
#include <QtCore/QRegularExpression>
#include <QtCore/QTimer>
#include <QtCore/QUrl>
#include <QtNetwork/QHttpMultiPart>
#include <QtNetwork/QHttpPart>
#include <QtNetwork/QNetworkAccessManager>
#include <QtNetwork/QNetworkReply>
#include <QtNetwork/QNetworkRequest>

#include <deque>
#include <optional>

namespace AyuFeatures::VoiceTranscribe {
namespace {

constexpr auto kMaxInFlight = 3;
constexpr auto kMaxRateLimitRetries = 6;
constexpr auto kMaxErrorRetries = 1;
constexpr auto kTimeoutMs = 90 * 1000;
constexpr auto kMaxFileSize = qint64(25) * 1024 * 1024;

const auto kDefaultPrompt = QString::fromUtf8(
	"Живая разговорная речь в личной переписке, с матом и сленгом, "
	"без цензуры. English stays in English. "
	"Растянутые гласные как слышно. "
	"Не выдумывай титры и субтитры; нет речи, нет текста.");

struct Config {
	QString key;
	QString baseUrl = u"https://api.groq.com/openai/v1"_q;
	QString model = u"whisper-large-v3"_q;
	QString language = u"ru"_q;
	QString prompt = kDefaultPrompt;
	bool autoTranscribe = true;
	bool enabled = true;
};

struct Job {
	QByteArray audio;
	QString fileName;
	std::function<void(Result)> done;
	int rateLimitRetries = 0;
	int errorRetries = 0;
};

std::deque<Job> Queue;
int InFlight = 0;

[[nodiscard]] QString ReadString(
		const QJsonObject &object,
		const char *name,
		const QString &fallback) {
	const auto value = object.value(QLatin1String(name));
	return value.isString() ? value.toString().trimmed() : fallback;
}

[[nodiscard]] bool ReadBool(
		const QJsonObject &object,
		const char *name,
		bool fallback) {
	const auto value = object.value(QLatin1String(name));
	return value.isBool() ? value.toBool() : fallback;
}

[[nodiscard]] Config LoadConfig() {
	auto result = Config();
	const auto paths = {
		cExeDir() + u"ayu_voice.json"_q,
		cWorkingDir() + u"tdata/ayu_voice.json"_q,
	};
	for (const auto &path : paths) {
		auto file = QFile(path);
		if (!file.open(QIODevice::ReadOnly)) {
			continue;
		}
		auto error = QJsonParseError();
		const auto document = QJsonDocument::fromJson(file.readAll(), &error);
		if (error.error != QJsonParseError::NoError || !document.isObject()) {
			LOG(("AyuVoice: bad config %1: %2").arg(path, error.errorString()));
			continue;
		}
		const auto object = document.object();
		result.key = ReadString(object, "key", result.key);
		result.baseUrl = ReadString(object, "base_url", result.baseUrl);
		result.model = ReadString(object, "model", result.model);
		result.language = ReadString(object, "language", result.language);
		result.prompt = ReadString(object, "prompt", result.prompt);
		result.autoTranscribe = ReadBool(object, "auto", true);
		result.enabled = ReadBool(object, "enabled", true);
		LOG(("AyuVoice: config %1, model %2, key %3"
			).arg(path, result.model, result.key.isEmpty() ? u"none"_q : u"set"_q));
		break;
	}
	if (result.key.isEmpty()) {
		const auto env = QProcessEnvironment::systemEnvironment();
		result.key = env.value(u"AYU_VOICE_KEY"_q, env.value(u"GROQ_API_KEY"_q));
	}
	LOG(("AyuVoice: own transcription %1"
		).arg((result.enabled && !result.key.isEmpty()) ? u"on"_q : u"off"_q));
	while (result.baseUrl.endsWith('/')) {
		result.baseUrl.chop(1);
	}
	return result;
}

[[nodiscard]] const Config &CurrentConfig() {
	static const auto result = LoadConfig();
	return result;
}

[[nodiscard]] QNetworkAccessManager *Manager() {
	static const auto result = new QNetworkAccessManager(
		QCoreApplication::instance());
	return result;
}

[[nodiscard]] QHttpPart FieldPart(const QString &name, const QString &value) {
	auto part = QHttpPart();
	part.setHeader(
		QNetworkRequest::ContentDispositionHeader,
		QVariant(u"form-data; name=\"%1\""_q.arg(name)));
	part.setBody(value.toUtf8());
	return part;
}

[[nodiscard]] QString MimeFor(const QString &fileName) {
	const auto lower = fileName.toLower();
	return lower.endsWith(u".mp4"_q)
		? u"video/mp4"_q
		: lower.endsWith(u".mp3"_q)
		? u"audio/mpeg"_q
		: lower.endsWith(u".m4a"_q)
		? u"audio/mp4"_q
		: lower.endsWith(u".wav"_q)
		? u"audio/wav"_q
		: u"audio/ogg"_q;
}

[[nodiscard]] bool VerboseSupported(const Config &config) {
	// gpt-4o-transcribe and friends answer only json/text.
	return !config.model.startsWith(u"gpt-"_q);
}

// Whisper writes text into silence and noise; verbose_json lets us drop
// segments the model itself marks as "no speech".
[[nodiscard]] QString TextFromReply(const QByteArray &body) {
	const auto document = QJsonDocument::fromJson(body);
	if (!document.isObject()) {
		return QString::fromUtf8(body).trimmed();
	}
	const auto object = document.object();
	const auto segments = object.value(u"segments"_q).toArray();
	if (segments.isEmpty()) {
		return object.value(u"text"_q).toString().trimmed();
	}
	auto parts = QStringList();
	for (const auto &value : segments) {
		const auto segment = value.toObject();
		const auto noSpeech = segment.value(u"no_speech_prob"_q).toDouble(0.);
		const auto logProb = segment.value(u"avg_logprob"_q).toDouble(0.);
		if (noSpeech > 0.85 || (noSpeech > 0.5 && logProb < -1.)) {
			continue;
		}
		const auto text = segment.value(u"text"_q).toString().trimmed();
		if (!text.isEmpty()) {
			parts.push_back(text);
		}
	}
	return parts.join(' ');
}

[[nodiscard]] Result Failure(Error error) {
	auto result = Result();
	result.error = error;
	return result;
}

void Pump();

void Finish(Job &&job, Result result) {
	--InFlight;
	if (job.done) {
		job.done(std::move(result));
	}
	Pump();
}

void RetryLater(Job &&job, int delayMs) {
	--InFlight;
	QTimer::singleShot(
		delayMs,
		QCoreApplication::instance(),
		[job = std::move(job)]() mutable {
			Queue.push_front(std::move(job));
			Pump();
		});
	Pump();
}

void Send(Job &&job) {
	const auto &config = CurrentConfig();
	++InFlight;

	auto multiPart = new QHttpMultiPart(QHttpMultiPart::FormDataType);
	auto filePart = QHttpPart();
	filePart.setHeader(
		QNetworkRequest::ContentTypeHeader,
		QVariant(MimeFor(job.fileName)));
	filePart.setHeader(
		QNetworkRequest::ContentDispositionHeader,
		QVariant(u"form-data; name=\"file\"; filename=\"%1\""_q.arg(
			job.fileName)));
	filePart.setBody(job.audio);
	multiPart->append(filePart);
	multiPart->append(FieldPart(u"model"_q, config.model));
	multiPart->append(FieldPart(u"temperature"_q, u"0"_q));
	multiPart->append(FieldPart(
		u"response_format"_q,
		VerboseSupported(config) ? u"verbose_json"_q : u"json"_q));
	if (!config.language.isEmpty()) {
		multiPart->append(FieldPart(u"language"_q, config.language));
	}
	if (!config.prompt.isEmpty()) {
		multiPart->append(FieldPart(u"prompt"_q, config.prompt));
	}

	auto request = QNetworkRequest(
		QUrl(config.baseUrl + u"/audio/transcriptions"_q));
	request.setRawHeader("Authorization", "Bearer " + config.key.toUtf8());
	request.setTransferTimeout(kTimeoutMs);

	const auto reply = Manager()->post(request, multiPart);
	multiPart->setParent(reply);

	auto shared = std::make_shared<Job>(std::move(job));
	QObject::connect(reply, &QNetworkReply::finished, reply, [=] {
		reply->deleteLater();
		const auto status = reply->attribute(
			QNetworkRequest::HttpStatusCodeAttribute).toInt();
		const auto body = reply->readAll();
		auto job = std::move(*shared);
		if (reply->error() == QNetworkReply::NoError && status == 200) {
			const auto text = CleanHallucinations(TextFromReply(body));
			auto result = Result();
			result.text = text;
			Finish(std::move(job), std::move(result));
			return;
		}
		LOG(("AyuVoice: status %1, error %2, body %3"
			).arg(status
			).arg(int(reply->error())
			).arg(QString::fromUtf8(body.left(300))));
		if (status == 429 && job.rateLimitRetries < kMaxRateLimitRetries) {
			++job.rateLimitRetries;
			const auto header = reply->rawHeader("retry-after").toDouble();
			const auto seconds = (header > 0.) ? std::min(header, 60.) : 3.;
			RetryLater(std::move(job), int(seconds * 1000) + 250);
		} else if (status == 413) {
			Finish(std::move(job), Failure(Error::TooLong));
		} else if ((status == 0 || status >= 500)
			&& job.errorRetries < kMaxErrorRetries) {
			++job.errorRetries;
			RetryLater(std::move(job), 1500);
		} else {
			Finish(std::move(job), Failure(Error::Failed));
		}
	});
}

void Pump() {
	while (InFlight < kMaxInFlight && !Queue.empty()) {
		auto job = std::move(Queue.front());
		Queue.pop_front();
		Send(std::move(job));
	}
}

} // namespace

bool Available() {
	const auto &config = CurrentConfig();
	return config.enabled && !config.key.isEmpty();
}

bool AutoTranscribe() {
	return Available() && CurrentConfig().autoTranscribe;
}

qint64 MaxFileSize() {
	return kMaxFileSize;
}

void Transcribe(
		QByteArray audio,
		QString fileName,
		std::function<void(Result)> done) {
	if (!Available() || audio.isEmpty()) {
		if (done) {
			done(Failure(Error::Failed));
		}
		return;
	} else if (audio.size() > kMaxFileSize) {
		if (done) {
			done(Failure(Error::TooLong));
		}
		return;
	}
	Queue.push_back(Job{
		.audio = std::move(audio),
		.fileName = std::move(fileName),
		.done = std::move(done),
	});
	Pump();
}

QString CleanHallucinations(const QString &text) {
	static const auto markers = {
		u"субтитры создавал"_q,
		u"субтитры сделал"_q,
		u"субтитры делал"_q,
		u"субтитры подготовил"_q,
		u"субтитры by"_q,
		u"subtitles by"_q,
		u"редактор субтитров"_q,
		u"корректор субтитров"_q,
		u"перевод субтитров"_q,
		u"dimatorzok"_q,
		u"dima torzok"_q,
		u"дима торзок"_q,
		u"amara.org"_q,
	};
	static const auto phrases = {
		u"спасибо за просмотр"_q,
		u"продолжение следует"_q,
		u"подписывайтесь на канал"_q,
		u"ставьте лайки и подписывайтесь"_q,
		u"thanks for watching"_q,
		u"thank you for watching"_q,
		u"like and subscribe"_q,
		u"please subscribe"_q,
		u"subscribe to my channel"_q,
		u"see you in the next video"_q,
	};
	static const auto sentenceEnd = QRegularExpression(
		u"(?<=[.!?…])\\s+"_q);
	static const auto nonLetters = QRegularExpression(
		u"[^\\p{L}\\p{N}]+"_q);

	auto kept = QStringList();
	for (const auto &sentence : text.split(sentenceEnd, Qt::SkipEmptyParts)) {
		const auto lower = sentence.toLower();
		auto bad = false;
		for (const auto &marker : markers) {
			if (lower.contains(marker)) {
				bad = true;
				break;
			}
		}
		if (!bad) {
			const auto plain = lower.split(
				nonLetters,
				Qt::SkipEmptyParts).join(' ');
			for (const auto &phrase : phrases) {
				if (plain == phrase) {
					bad = true;
					break;
				}
			}
		}
		if (!bad) {
			kept.push_back(sentence.trimmed());
		}
	}
	return kept.join(' ').trimmed();
}

} // namespace AyuFeatures::VoiceTranscribe
