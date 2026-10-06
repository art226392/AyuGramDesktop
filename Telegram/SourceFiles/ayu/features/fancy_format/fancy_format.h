// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#pragma once

#include <memory>

namespace Main {
class SessionShow;
} // namespace Main

namespace Ui {
class InputField;
} // namespace Ui

// One click fancy formatting of the message being typed: Ctrl+Shift+F or
// "✨ Красиво (Codex)" in the field context menu sends the text (or only
// the selection) to Codex and puts the answer back with bold, quotes,
// spoilers and the rest. Ctrl+Z returns the original text.
//
// Engines, in order for "auto":
//   codex - local Codex CLI (`codex exec`, ChatGPT login, no API key);
//   groq  - any OpenAI-compatible /chat/completions, Groq by default,
//           the key may be shared with ayu_voice.json.
//
// Configuration is read on every run from the first file that exists:
//   <exe dir>/ayu_fancy.json
//   <working dir>/tdata/ayu_fancy.json
// {
//   "enabled": true,
//   "engine": "auto",                       ("codex", "groq")
//   "codex_path": "",                       (found in PATH and npm if empty)
//   "codex_model": "",                      (Codex default if empty)
//   "codex_effort": "low",
//   "key": "gsk_...",                       (else ayu_voice.json, GROQ_API_KEY)
//   "base_url": "https://api.groq.com/openai/v1",
//   "model": "openai/gpt-oss-120b",
//   "style": ""                             (extra wishes for every message)
// }
namespace AyuFeatures::FancyFormat {

void SetupField(
	std::shared_ptr<Main::SessionShow> show,
	not_null<Ui::InputField*> field);

} // namespace AyuFeatures::FancyFormat
