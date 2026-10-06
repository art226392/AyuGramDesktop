// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#include "ayu/features/fancy_format/fancy_format_text.h"

#include "ui/text/text_html_tags.h"
#include "ui/widgets/fields/input_field.h"

#include <QtCore/QRegularExpression>

#include <algorithm>

namespace AyuFeatures::FancyFormat {
namespace {

[[nodiscard]] QString Token(int index) {
	return QString(QChar(0x27E6)) + QString::number(index) + QChar(0x27E7);
}

[[nodiscard]] int CountVisible(const QString &text) {
	auto result = 0;
	for (const auto ch : text) {
		if (!ch.isSpace()) {
			++result;
		}
	}
	return result;
}

// Positions of tags after [from, till) became something of newLength.
void ShiftTags(
		TextWithTags::Tags &tags,
		int from,
		int till,
		int newLength) {
	const auto delta = newLength - (till - from);
	for (auto &tag : tags) {
		auto start = tag.offset;
		auto end = tag.offset + tag.length;
		if (end <= from) {
			continue;
		} else if (start >= till) {
			start += delta;
			end += delta;
		} else {
			start = std::min(start, from);
			end = (end >= till) ? (end + delta) : (from + newLength);
		}
		tag.offset = start;
		tag.length = end - start;
	}
}

[[nodiscard]] QString NewlinesToBreaks(QString html) {
	static const auto blockAfter = QRegularExpression(
		u"(</(?:blockquote|pre|p|div|ul|ol|li|h[1-6])>)[ \\t]*\\r?\\n"_q,
		QRegularExpression::CaseInsensitiveOption);
	static const auto blockBefore = QRegularExpression(
		u"\\r?\\n[ \\t]*(?=</?(?:blockquote|pre|p|div|ul|ol|li|h[1-6])\\b)"_q,
		QRegularExpression::CaseInsensitiveOption);
	static const auto breakNewline = QRegularExpression(
		u"<br\\s*/?>[ \\t]*\\r?\\n"_q,
		QRegularExpression::CaseInsensitiveOption);
	static const auto newline = QRegularExpression(u"\\r?\\n"_q);

	html.replace(blockAfter, u"\\1"_q);
	html.replace(blockBefore, QString());
	html.replace(breakNewline, u"<br>"_q);
	html.replace(newline, u"<br>"_q);
	return html;
}

} // namespace

bool IsProtectedTag(QStringView tagId) {
	for (const auto &part : TextUtilities::SplitTags(tagId)) {
		if (part.startsWith(Ui::InputField::kCustomEmojiTagStart)
			|| part.startsWith(Ui::InputField::kCustomDateTagStart)
			|| TextUtilities::IsMentionLink(part)) {
			return true;
		}
	}
	return false;
}

ProtectedText Protect(const TextWithTags &text) {
	struct Range {
		int from = 0;
		int till = 0;
		QString id;
	};
	const auto size = int(text.text.size());
	auto ranges = std::vector<Range>();
	for (const auto &tag : text.tags) {
		const auto from = std::clamp(tag.offset, 0, size);
		const auto till = std::clamp(tag.offset + tag.length, 0, size);
		if (till > from && IsProtectedTag(tag.id)) {
			ranges.push_back({ from, till, tag.id });
		}
	}
	std::sort(ranges.begin(), ranges.end(), [](const auto &a, const auto &b) {
		return a.from < b.from;
	});

	auto result = ProtectedText();
	auto tags = TextWithTags::Tags();
	for (const auto &tag : text.tags) {
		if (!IsProtectedTag(tag.id)) {
			tags.push_back(tag);
		}
	}
	auto out = QString();
	out.reserve(size);
	auto position = 0;
	auto delta = 0;
	for (const auto &range : ranges) {
		if (range.from < position) {
			continue; // overlaps the previous one
		}
		out.append(QStringView(text.text).mid(position, range.from - position));
		const auto token = Token(int(result.items.size()) + 1);
		result.items.push_back({
			.token = token,
			.text = text.text.mid(range.from, range.till - range.from),
			.tagId = range.id,
		});
		const auto shiftedFrom = range.from + delta;
		ShiftTags(tags, shiftedFrom, range.till + delta, token.size());
		out.append(token);
		delta += token.size() - (range.till - range.from);
		position = range.till;
	}
	out.append(QStringView(text.text).mid(position));
	result.text = { out, CanonicalTags(tags, out.size()) };
	return result;
}

Restored Restore(TextWithTags text, const std::vector<Protected> &items) {
	auto result = Restored();
	for (const auto &item : items) {
		const auto position = text.text.indexOf(item.token);
		if (position < 0) {
			++result.lost;
			continue;
		}
		const auto till = position + int(item.token.size());
		text.text.replace(position, item.token.size(), item.text);
		ShiftTags(text.tags, position, till, item.text.size());
		text.tags.push_back({
			int(position),
			int(item.text.size()),
			item.tagId,
		});
	}
	text.tags = CanonicalTags(text.tags, text.text.size());
	result.text = std::move(text);
	return result;
}

TextWithTags::Tags CanonicalTags(
		const TextWithTags::Tags &tags,
		int textSize) {
	auto points = std::vector<int>{ 0, textSize };
	for (const auto &tag : tags) {
		points.push_back(std::clamp(tag.offset, 0, textSize));
		points.push_back(std::clamp(tag.offset + tag.length, 0, textSize));
	}
	std::sort(points.begin(), points.end());
	points.erase(std::unique(points.begin(), points.end()), points.end());

	auto result = TextWithTags::Tags();
	for (auto i = 0; i + 1 < int(points.size()); ++i) {
		const auto from = points[i];
		const auto till = points[i + 1];
		auto id = QString();
		for (const auto &tag : tags) {
			if (tag.offset <= from && tag.offset + tag.length >= till) {
				for (const auto &part : TextUtilities::SplitTags(tag.id)) {
					if (!part.isEmpty()) {
						id = TextUtilities::TagWithAdded(id, part.toString());
					}
				}
			}
		}
		if (id.isEmpty()) {
			continue;
		} else if (!result.isEmpty()
			&& result.back().id == id
			&& result.back().offset + result.back().length == from) {
			result.back().length += till - from;
		} else {
			result.push_back({ from, till - from, id });
		}
	}
	return result;
}

QString ToHtml(const TextWithTags &text) {
	auto result = TextUtilities::TextWithTagsToHtml(text);
	if (!result.isEmpty() || text.text.isEmpty()) {
		return result;
	}
	// No supported tags at all, the converter returns nothing then.
	static const auto newline = QRegularExpression(u"\\r?\\n"_q);
	return TextUtilities::EscapeForHtml(text.text).replace(newline, u"<br>"_q);
}

QString CleanAnswer(QString answer) {
	answer = answer.trimmed();
	static const auto fenced = QRegularExpression(
		u"```[A-Za-z0-9_-]*[ \\t]*\\r?\\n(.*?)\\r?\\n?```"_q,
		QRegularExpression::DotMatchesEverythingOption);
	if (const auto match = fenced.match(answer); match.hasMatch()) {
		const auto inside = match.captured(1).trimmed();
		// Only when the fence wraps the answer, not a code sample in it.
		if (match.capturedStart() == 0 || !answer.contains(u"<pre"_q)) {
			answer = inside;
		}
	}
	if (answer.startsWith(u"<<<"_q)) {
		answer = answer.mid(3);
	}
	if (answer.endsWith(u">>>"_q)) {
		answer.chop(3);
	}
	answer = answer.trimmed();

	// Newlines are plain whitespace for an HTML parser, <pre> keeps them.
	static const auto pre = QRegularExpression(
		u"<pre\\b.*?</pre>"_q,
		QRegularExpression::CaseInsensitiveOption
			| QRegularExpression::DotMatchesEverythingOption);
	auto result = QString();
	auto position = 0;
	auto i = pre.globalMatch(answer);
	while (i.hasNext()) {
		const auto match = i.next();
		result.append(NewlinesToBreaks(
			answer.mid(position, match.capturedStart() - position)));
		result.append(match.captured(0));
		position = match.capturedEnd();
	}
	result.append(NewlinesToBreaks(answer.mid(position)));
	return result;
}

TextWithTags FromHtml(const QString &html) {
	auto result = TextUtilities::TextWithTagsFromHtmlFragment(html);
	auto size = int(result.text.size());
	while (size > 0 && result.text[size - 1].isSpace()) {
		--size;
	}
	if (size < result.text.size()) {
		result.text.truncate(size);
		for (auto &tag : result.tags) {
			tag.length = std::clamp(tag.offset + tag.length, 0, size)
				- std::min(tag.offset, size);
			tag.offset = std::min(tag.offset, size);
		}
	}
	result.tags = CanonicalTags(result.tags, result.text.size());
	return result;
}

TextWithTags Splice(
		const TextWithTags &full,
		int from,
		int till,
		const TextWithTags &with) {
	const auto size = int(full.text.size());
	from = std::clamp(from, 0, size);
	till = std::clamp(till, from, size);
	const auto delta = int(with.text.size()) - (till - from);

	auto result = TextWithTags();
	result.text = full.text.left(from) + with.text + full.text.mid(till);
	for (const auto &tag : full.tags) {
		const auto start = tag.offset;
		const auto end = tag.offset + tag.length;
		if (start < from) {
			result.tags.push_back({ start, std::min(end, from) - start, tag.id });
		}
		if (end > till) {
			const auto left = std::max(start, till);
			result.tags.push_back({ left + delta, end - left, tag.id });
		}
	}
	for (const auto &tag : with.tags) {
		result.tags.push_back({ tag.offset + from, tag.length, tag.id });
	}
	result.tags = CanonicalTags(result.tags, result.text.size());
	return result;
}

QString BuildPrompt(const QString &html, const QString &extra) {
	auto result = QString::fromUtf8(R"(You format Telegram messages. Rewrite the message below as one beautifully formatted Telegram message.

Rules:
- Keep the author's language, voice, slang, profanity and meaning. Do not translate, censor, summarize, shorten, or add facts, greetings, signatures, hashtags or questions.
- Fix only obvious typos and punctuation.
- Make it easy to scan: short paragraphs with an empty line between them; <b>bold</b> for the key idea and key words; <i>italic</i> for nuance; <u>underline</u> rarely; <s>strike</s> for a self-correction joke; <tg-spoiler>spoiler</tg-spoiler> for a punchline or a spoiler; <code>code</code> for commands, numbers to copy, file names; <pre>...</pre> for multi-line code; <blockquote>...</blockquote> for quotes; <a href="...">text</a> keeps existing links.
- Lists: one item per line, each line starts with "• " or one fitting emoji. Never use <ul>, <ol>, <li>, <h1>-<h6>, <p>, <div>, tables or images.
- Emoji: at most one per paragraph and only where natural; none in formal or sad text.
- A short message (one or two sentences) stays short: only add emphasis.
- Tokens like ⟦1⟧ are custom emoji or mentions: copy each one exactly once, unchanged, at the same place.
- Line breaks only as <br>; an empty line is <br><br>.
- Answer with the formatted message only: no explanations, no ``` fences, no <<< >>> markers. Do not run any commands or tools.
)");
	if (!extra.trimmed().isEmpty()) {
		result += u"- Extra wishes of the author: "_q + extra.trimmed() + '\n';
	}
	result += u"\nMessage (HTML):\n<<<\n"_q + html + u"\n>>>\n"_q;
	return result;
}

bool LooksSane(const TextWithTags &original, const TextWithTags &result) {
	const auto was = CountVisible(original.text);
	const auto now = CountVisible(result.text);
	if (!now) {
		return false;
	} else if (was >= 20 && now * 2 < was) {
		return false;
	}
	return (now <= was * 3 + 300);
}

} // namespace AyuFeatures::FancyFormat
