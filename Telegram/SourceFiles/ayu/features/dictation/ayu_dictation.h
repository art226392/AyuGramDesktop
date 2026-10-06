// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#pragma once

namespace Ayu::Dictation {

// Voice dictation into the focused input field.
// Speech is recognized by OpenAI, the first Enter after dictation
// polishes the text with Telegram AI, the second Enter sends it.
void init();

[[nodiscard]] QString apiKey();
[[nodiscard]] bool hasBuiltInKey();

} // namespace Ayu::Dictation
