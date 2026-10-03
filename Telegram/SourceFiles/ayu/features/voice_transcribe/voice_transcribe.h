// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QString>

#include <functional>

// Voice and round message transcription through any OpenAI-compatible
// /audio/transcriptions endpoint (Groq Whisper by default), so it works
// in private chats and without Premium.
//
// Configuration is read once from the first file that exists:
//   <exe dir>/ayu_voice.json
//   <working dir>/tdata/ayu_voice.json
// {
//   "key": "gsk_...",                       (or env GROQ_API_KEY)
//   "base_url": "https://api.groq.com/openai/v1",
//   "model": "whisper-large-v3",
//   "language": "ru",                       ("" for auto detect)
//   "prompt": "...",
//   "auto": true,                           (transcribe without a click)
//   "enabled": true
// }
namespace AyuFeatures::VoiceTranscribe {

enum class Error {
	None,
	TooLong,
	Failed,
};

struct Result {
	QString text;
	Error error = Error::None;
};

[[nodiscard]] bool Available();
[[nodiscard]] bool AutoTranscribe();

// Larger files are reported as Error::TooLong without a request.
[[nodiscard]] qint64 MaxFileSize();

// Queued, at most a few requests in flight, 429 answers are retried after
// the server's Retry-After. The callback is invoked on the main thread.
void Transcribe(
	QByteArray audio,
	QString fileName,
	std::function<void(Result)> done);

// Drops subtitle credits and other Whisper hallucinations on silence.
[[nodiscard]] QString CleanHallucinations(const QString &text);

} // namespace AyuFeatures::VoiceTranscribe
