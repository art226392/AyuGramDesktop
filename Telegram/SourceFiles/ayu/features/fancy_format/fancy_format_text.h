// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#pragma once

#include "ui/text/text_entity.h"

#include <vector>

// Text side of the one click fancy formatting: what goes to the model and
// how its answer becomes formatted text in the message field again.
// No widgets and no network here, so it is checked by a plain harness.
namespace AyuFeatures::FancyFormat {

// Custom emoji, mentions and custom dates can't survive an HTML round trip,
// so they travel as ⟦1⟧, ⟦2⟧... and are put back after the answer.
struct Protected {
	QString token;
	QString text;
	QString tagId;
};

struct ProtectedText {
	TextWithTags text;
	std::vector<Protected> items;
};

[[nodiscard]] bool IsProtectedTag(QStringView tagId);
[[nodiscard]] ProtectedText Protect(const TextWithTags &text);

struct Restored {
	TextWithTags text;
	int lost = 0; // tokens the model dropped
};
[[nodiscard]] Restored Restore(
	TextWithTags text,
	const std::vector<Protected> &items);

// Overlapping tags become one non overlapping run per set of tags,
// joined the way the input field expects ("**|__").
[[nodiscard]] TextWithTags::Tags CanonicalTags(
	const TextWithTags::Tags &tags,
	int textSize);

// Message field text as HTML for the prompt, plain text escaped as well.
[[nodiscard]] QString ToHtml(const TextWithTags &text);

// The model answer back to field text: drops ``` fences and chatter
// around them, turns bare newlines into <br> outside of <pre>.
[[nodiscard]] QString CleanAnswer(QString answer);
[[nodiscard]] TextWithTags FromHtml(const QString &html);

// [from, till) of the field text replaced, tags outside are kept as is.
[[nodiscard]] TextWithTags Splice(
	const TextWithTags &full,
	int from,
	int till,
	const TextWithTags &with);

[[nodiscard]] QString BuildPrompt(const QString &html, const QString &extra);

// Refuses answers that lost most of the text or every placeholder.
[[nodiscard]] bool LooksSane(
	const TextWithTags &original,
	const TextWithTags &result);

} // namespace AyuFeatures::FancyFormat
