// AyuFancy: one click fancy formatting for AyuGram / Telegram Desktop.
//
// Ctrl+Shift+F in the AyuGram window: the message field text (or only the
// selection) is copied, formatted by Codex (Groq as a spare engine) and
// pasted back with bold, italic, quotes, spoilers, code and lists through
// Telegram's own clipboard format, so the installed client works as is.
// Ctrl+Z in the field returns the original text.
//
// C# 5 on purpose: built on the user's PC by csc.exe of .NET Framework 4.
//   csc /codepage:65001 /target:winexe /out:AyuFancy.exe /r:System.Web.Extensions.dll
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll AyuFancy.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AyuFancy {

public class Tag {
	public int Offset;
	public int Length;
	public string Id;

	public Tag(int offset, int length, string id) {
		Offset = offset;
		Length = length;
		Id = id;
	}
}

public class Tagged {
	public string Text = "";
	public List<Tag> Tags = new List<Tag>();

	public Tagged() {
	}

	public Tagged(string text, List<Tag> tags) {
		Text = text ?? "";
		Tags = tags ?? new List<Tag>();
	}
}

public class Protected {
	public string Token;
	public string Text;
	public string TagId;
}

// Text side, mirrors ayu/features/fancy_format/fancy_format_text.cpp and
// the tag ids of Ui::InputField.
public static class Core {
	public const string Bold = "**";
	public const string Italic = "__";
	public const string Underline = "^^";
	public const string Strike = "~~";
	public const string Code = "`";
	public const string Pre = "```";
	public const string Spoiler = "||";
	public const string Quote = ">";
	public const string QuoteCollapsed = ">^";
	public const char Separator = '\\';

	public const string TextMime = "application/x-td-field-text";
	public const string TagsMime = "application/x-td-field-tags";

	public static List<string> SplitTags(string id) {
		var result = new List<string>();
		if (string.IsNullOrEmpty(id)) {
			return result;
		}
		foreach (var part in id.Split(Separator)) {
			if (part.Length > 0) {
				result.Add(part);
			}
		}
		return result;
	}

	public static string JoinTags(IEnumerable<string> parts) {
		var list = parts.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
		list.Sort(string.CompareOrdinal);
		return string.Join(Separator.ToString(), list);
	}

	public static bool IsProtectedPart(string part) {
		return part.StartsWith("custom-emoji://", StringComparison.Ordinal)
			|| part.StartsWith("custom-date://", StringComparison.Ordinal)
			|| part.StartsWith("mention://", StringComparison.Ordinal);
	}

	public static bool IsProtectedTag(string id) {
		return SplitTags(id).Any(p => IsProtectedPart(p));
	}

	public static bool IsLinkPart(string part) {
		return Regex.IsMatch(part, "^(https?|tg|mailto|ftp)://?", RegexOptions.IgnoreCase)
			|| Regex.IsMatch(part, "^[A-Za-z0-9.-]+\\.[A-Za-z]{2,}(/|$)");
	}

	static string Token(int index) {
		return "\u27E6" + index + "\u27E7";
	}

	static int CountVisible(string text) {
		var result = 0;
		foreach (var ch in text) {
			if (!char.IsWhiteSpace(ch)) {
				++result;
			}
		}
		return result;
	}

	// Positions of tags after [from, till) became something of newLength.
	static void ShiftTags(List<Tag> tags, int from, int till, int newLength) {
		var delta = newLength - (till - from);
		foreach (var tag in tags) {
			var start = tag.Offset;
			var end = tag.Offset + tag.Length;
			if (end <= from) {
				continue;
			} else if (start >= till) {
				start += delta;
				end += delta;
			} else {
				start = Math.Min(start, from);
				end = (end >= till) ? (end + delta) : (from + newLength);
			}
			tag.Offset = start;
			tag.Length = end - start;
		}
	}

	public static List<Tag> Canonical(List<Tag> tags, int size) {
		var points = new SortedSet<int> { 0, size };
		foreach (var tag in tags) {
			points.Add(Math.Max(0, Math.Min(size, tag.Offset)));
			points.Add(Math.Max(0, Math.Min(size, tag.Offset + tag.Length)));
		}
		var list = points.ToList();
		var result = new List<Tag>();
		for (var i = 0; i + 1 < list.Count; ++i) {
			var from = list[i];
			var till = list[i + 1];
			var parts = new List<string>();
			foreach (var tag in tags) {
				if (tag.Offset <= from && tag.Offset + tag.Length >= till) {
					parts.AddRange(SplitTags(tag.Id));
				}
			}
			var id = JoinTags(parts);
			if (id.Length == 0) {
				continue;
			}
			var last = result.Count > 0 ? result[result.Count - 1] : null;
			if (last != null && last.Id == id && last.Offset + last.Length == from) {
				last.Length += till - from;
			} else {
				result.Add(new Tag(from, till - from, id));
			}
		}
		return result;
	}

	public static Tagged Protect(Tagged text, List<Protected> items) {
		var size = text.Text.Length;
		var ranges = text.Tags
			.Where(t => IsProtectedTag(t.Id))
			.Select(t => new Tag(
				Math.Max(0, Math.Min(size, t.Offset)),
				Math.Max(0, Math.Min(size, t.Offset + t.Length)),
				t.Id))
			.Where(r => r.Length > r.Offset)
			.OrderBy(r => r.Offset)
			.ToList();
		var tags = text.Tags
			.Where(t => !IsProtectedTag(t.Id))
			.Select(t => new Tag(t.Offset, t.Length, t.Id))
			.ToList();
		var result = new StringBuilder();
		var position = 0;
		var delta = 0;
		foreach (var range in ranges) {
			var from = range.Offset;
			var till = range.Length; // "Length" holds the end here
			if (from < position) {
				continue;
			}
			result.Append(text.Text, position, from - position);
			var token = Token(items.Count + 1);
			items.Add(new Protected {
				Token = token,
				Text = text.Text.Substring(from, till - from),
				TagId = range.Id,
			});
			ShiftTags(tags, from + delta, till + delta, token.Length);
			result.Append(token);
			delta += token.Length - (till - from);
			position = till;
		}
		result.Append(text.Text, position, size - position);
		var output = result.ToString();
		return new Tagged(output, Canonical(tags, output.Length));
	}

	public static Tagged Restore(Tagged text, List<Protected> items, out int lost) {
		lost = 0;
		var value = text.Text;
		var tags = text.Tags.Select(t => new Tag(t.Offset, t.Length, t.Id)).ToList();
		foreach (var item in items) {
			var position = value.IndexOf(item.Token, StringComparison.Ordinal);
			if (position < 0) {
				++lost;
				continue;
			}
			var till = position + item.Token.Length;
			value = value.Substring(0, position) + item.Text + value.Substring(till);
			ShiftTags(tags, position, till, item.Text.Length);
			tags.Add(new Tag(position, item.Text.Length, item.TagId));
		}
		return new Tagged(value, Canonical(tags, value.Length));
	}

	// Order in which tags open, outermost first.
	static int PartOrder(string part) {
		if (part == QuoteCollapsed || part == Quote) return 0;
		if (part.StartsWith(Pre, StringComparison.Ordinal)) return 1;
		if (IsLinkPart(part)) return 2;
		if (part == Bold) return 3;
		if (part == Italic) return 4;
		if (part == Underline) return 5;
		if (part == Strike) return 6;
		if (part == Spoiler) return 7;
		if (part == Code) return 8;
		return -1;
	}

	static string OpenTag(string part) {
		if (part == Quote) return "<blockquote>";
		if (part == QuoteCollapsed) return "<blockquote expandable>";
		if (part.StartsWith(Pre, StringComparison.Ordinal)) {
			var language = part.Substring(Pre.Length);
			return language.Length > 0
				? "<pre><code class=\"language-" + Escape(language) + "\">"
				: "<pre>";
		}
		if (part == Bold) return "<b>";
		if (part == Italic) return "<i>";
		if (part == Underline) return "<u>";
		if (part == Strike) return "<s>";
		if (part == Spoiler) return "<tg-spoiler>";
		if (part == Code) return "<code>";
		return "<a href=\"" + Escape(part) + "\">";
	}

	static string CloseTag(string part) {
		if (part == Quote || part == QuoteCollapsed) return "</blockquote>";
		if (part.StartsWith(Pre, StringComparison.Ordinal)) {
			return part.Length > Pre.Length ? "</code></pre>" : "</pre>";
		}
		if (part == Bold) return "</b>";
		if (part == Italic) return "</i>";
		if (part == Underline) return "</u>";
		if (part == Strike) return "</s>";
		if (part == Spoiler) return "</tg-spoiler>";
		if (part == Code) return "</code>";
		return "</a>";
	}

	public static string Escape(string text) {
		return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
	}

	public static string ToHtml(Tagged text) {
		var size = text.Text.Length;
		var tags = Canonical(text.Tags, size);
		var points = new SortedSet<int> { 0, size };
		foreach (var tag in tags) {
			points.Add(tag.Offset);
			points.Add(tag.Offset + tag.Length);
		}
		var list = points.ToList();
		var result = new StringBuilder();
		var open = new List<string>();
		for (var i = 0; i + 1 < list.Count; ++i) {
			var from = list[i];
			var till = list[i + 1];
			var wanted = new List<string>();
			foreach (var tag in tags) {
				if (tag.Offset <= from && tag.Offset + tag.Length >= till) {
					wanted.AddRange(SplitTags(tag.Id).Where(p => PartOrder(p) >= 0));
				}
			}
			wanted = wanted.Distinct().OrderBy(p => PartOrder(p)).ToList();
			var keep = 0;
			while (keep < open.Count && keep < wanted.Count && open[keep] == wanted[keep]) {
				++keep;
			}
			var closedBlock = false;
			for (var j = open.Count - 1; j >= keep; --j) {
				result.Append(CloseTag(open[j]));
				closedBlock = closedBlock || PartOrder(open[j]) <= 1;
			}
			open.RemoveRange(keep, open.Count - keep);
			for (var j = keep; j < wanted.Count; ++j) {
				result.Append(OpenTag(wanted[j]));
				open.Add(wanted[j]);
			}
			var inPre = open.Any(p => p.StartsWith(Pre, StringComparison.Ordinal));
			var chunk = Escape(text.Text.Substring(from, till - from)).Replace("\r\n", "\n");
			if (closedBlock && chunk.StartsWith("\n", StringComparison.Ordinal)) {
				chunk = chunk.Substring(1); // a closed quote or pre already ends the line
			}
			result.Append(inPre ? chunk : chunk.Replace("\n", "<br>"));
		}
		for (var j = open.Count - 1; j >= 0; --j) {
			result.Append(CloseTag(open[j]));
		}
		return result.ToString();
	}

	static string NewlinesToBreaks(string html) {
		html = Regex.Replace(html, "(</(?:blockquote|pre|p|div|ul|ol|li|h[1-6])>)[ \\t]*\\r?\\n", "$1", RegexOptions.IgnoreCase);
		html = Regex.Replace(html, "\\r?\\n[ \\t]*(?=</?(?:blockquote|pre|p|div|ul|ol|li|h[1-6])\\b)", "", RegexOptions.IgnoreCase);
		html = Regex.Replace(html, "<br\\s*/?>[ \\t]*\\r?\\n", "<br>", RegexOptions.IgnoreCase);
		return Regex.Replace(html, "\\r?\\n", "<br>");
	}

	public static string CleanAnswer(string answer) {
		answer = (answer ?? "").Trim();
		var fenced = Regex.Match(answer, "```[A-Za-z0-9_-]*[ \\t]*\\r?\\n(.*?)\\r?\\n?```", RegexOptions.Singleline);
		if (fenced.Success && (fenced.Index == 0 || answer.IndexOf("<pre", StringComparison.OrdinalIgnoreCase) < 0)) {
			answer = fenced.Groups[1].Value.Trim();
		}
		if (answer.StartsWith("<<<", StringComparison.Ordinal)) answer = answer.Substring(3);
		if (answer.EndsWith(">>>", StringComparison.Ordinal)) answer = answer.Substring(0, answer.Length - 3);
		answer = answer.Trim();
		var result = new StringBuilder();
		var position = 0;
		foreach (Match match in Regex.Matches(answer, "<pre\\b.*?</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
			result.Append(NewlinesToBreaks(answer.Substring(position, match.Index - position)));
			result.Append(match.Value);
			position = match.Index + match.Length;
		}
		result.Append(NewlinesToBreaks(answer.Substring(position)));
		return result.ToString();
	}

	class Element {
		public string Name;
		public string TagId;
		public int Start;
		public bool Block;
		public bool Ordered;
		public int Counter;
	}

	static readonly HashSet<string> BlockNames = new HashSet<string> {
		"p", "div", "blockquote", "pre", "ul", "ol", "li",
		"h1", "h2", "h3", "h4", "h5", "h6", "section", "article",
	};

	static string Attribute(string attributes, string name) {
		var match = Regex.Match(attributes, "\\b" + name + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
		if (!match.Success) {
			return null;
		}
		var value = match.Groups[2].Success ? match.Groups[2].Value
			: match.Groups[3].Success ? match.Groups[3].Value
			: match.Groups[4].Value;
		return WebUtility.HtmlDecode(value);
	}

	static bool HasAttribute(string attributes, string name) {
		return Regex.IsMatch(attributes, "(^|\\s)" + name + "(\\s|=|$)", RegexOptions.IgnoreCase);
	}

	// Telegram HTML subset to field text with tags. Whitespace like a browser:
	// runs collapse to one space, <br> and blocks make line breaks, <pre> keeps
	// everything as is.
	public static Tagged FromHtml(string html) {
		return FromHtml(html, true);
	}

	public static Tagged FromHtml(string html, bool boldHeadings) {
		var text = new StringBuilder();
		var tags = new List<Tag>();
		var stack = new List<Element>();
		var pendingSpace = false;
		var pendingBreak = false;
		Func<bool> inPre = () => stack.Any(e => e.Name == "pre");
		Action ensureLine = () => {
			if (text.Length > 0 && text[text.Length - 1] != '\n') {
				text.Append('\n');
			}
			pendingSpace = false;
		};
		Action<string> appendText = (raw) => {
			var decoded = WebUtility.HtmlDecode(raw).Replace("\u00A0", " ");
			if (inPre()) {
				if (pendingBreak) { ensureLine(); pendingBreak = false; }
				text.Append(decoded.Replace("\r\n", "\n"));
				pendingSpace = false;
				return;
			}
			foreach (var ch in decoded) {
				if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') {
					pendingSpace = true;
					continue;
				}
				if (pendingBreak) {
					ensureLine();
					pendingBreak = false;
				}
				if (pendingSpace) {
					if (text.Length > 0 && text[text.Length - 1] != '\n') {
						text.Append(' ');
					}
					pendingSpace = false;
				}
				text.Append(ch);
			}
		};
		Action<Element> close = (element) => {
			var index = stack.LastIndexOf(element);
			if (index < 0) {
				return;
			}
			for (var i = stack.Count - 1; i >= index; --i) {
				var popped = stack[i];
				stack.RemoveAt(i);
				var end = text.Length;
				if (popped.TagId != null && end > popped.Start) {
					tags.Add(new Tag(popped.Start, end - popped.Start, popped.TagId));
				}
				if (popped.Block) {
					pendingBreak = true;
					pendingSpace = false;
				}
			}
		};

		var position = 0;
		var tagRegex = new Regex("<(/?)([A-Za-z][A-Za-z0-9-]*)([^>]*)>|<!--.*?-->", RegexOptions.Singleline);
		foreach (Match match in tagRegex.Matches(html ?? "")) {
			if (match.Index > position) {
				appendText(html.Substring(position, match.Index - position));
			}
			position = match.Index + match.Length;
			if (!match.Groups[2].Success || match.Groups[2].Length == 0) {
				continue; // comment
			}
			var closing = match.Groups[1].Value == "/";
			var name = match.Groups[2].Value.ToLowerInvariant();
			var attributes = match.Groups[3].Value;
			if (closing) {
				var element = stack.LastOrDefault(e => e.Name == name);
				if (element != null) {
					close(element);
				}
				continue;
			}
			if (name == "br") {
				if (pendingBreak) ensureLine();
				pendingBreak = false;
				text.Append('\n');
				pendingSpace = false;
				continue;
			}
			if (name == "hr") {
				ensureLine();
				pendingBreak = true;
				continue;
			}
			if (name == "img") {
				var alt = Attribute(attributes, "alt");
				if (!string.IsNullOrEmpty(alt)) appendText(alt);
				continue;
			}
			var block = BlockNames.Contains(name);
			if (block && name != "li") {
				if (text.Length > 0) {
					ensureLine();
				}
				pendingBreak = false;
			}
			var created = new Element { Name = name, Block = block };
			var heading = name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]);
			if (name == "b" || name == "strong" || (heading && boldHeadings)) {
				created.TagId = Bold;
			} else if (name == "i" || name == "em" || name == "cite") {
				created.TagId = Italic;
			} else if (name == "u" || name == "ins") {
				created.TagId = Underline;
			} else if (name == "s" || name == "strike" || name == "del") {
				created.TagId = Strike;
			} else if (name == "tg-spoiler" || (name == "span" && (Attribute(attributes, "class") ?? "").Contains("tg-spoiler"))) {
				created.TagId = Spoiler;
			} else if (name == "code") {
				var pre = stack.LastOrDefault(e => e.Name == "pre");
				if (pre != null) {
					var language = Regex.Match(Attribute(attributes, "class") ?? "", "language-([A-Za-z0-9_+#.-]+)");
					if (language.Success) {
						pre.TagId = Pre + language.Groups[1].Value;
					}
				} else {
					created.TagId = Code;
				}
			} else if (name == "pre") {
				created.TagId = Pre;
				var language = Attribute(attributes, "language");
				if (!string.IsNullOrEmpty(language)) created.TagId = Pre + language;
			} else if (name == "blockquote") {
				created.TagId = HasAttribute(attributes, "expandable") ? QuoteCollapsed : Quote;
			} else if (name == "a") {
				var href = Attribute(attributes, "href");
				if (!string.IsNullOrEmpty(href) && IsLinkPart(href)) created.TagId = href;
			} else if (name == "ol") {
				created.Ordered = true;
			}
			if (name == "li") {
				if (pendingBreak || (text.Length > 0 && text[text.Length - 1] != '\n')) {
					ensureLine();
				}
				pendingBreak = false;
				var list = stack.LastOrDefault(e => e.Name == "ul" || e.Name == "ol");
				if (list != null && list.Ordered) {
					list.Counter++;
					text.Append(list.Counter + ". ");
				} else {
					text.Append("\u2022 ");
				}
				pendingSpace = false;
			}
			if (!block && name != "li") {
				// A space before an inline tag stays outside of it.
				if (pendingBreak) {
					ensureLine();
					pendingBreak = false;
				}
				if (pendingSpace && text.Length > 0 && text[text.Length - 1] != '\n') {
					text.Append(' ');
				}
				pendingSpace = false;
			}
			created.Start = text.Length;
			stack.Add(created);
		}
		if (position < (html ?? "").Length) {
			appendText(html.Substring(position));
		}
		while (stack.Count > 0) {
			close(stack[stack.Count - 1]);
		}

		// Trailing whitespace goes, tags are clipped to the text.
		var value = text.ToString();
		var size = value.Length;
		while (size > 0 && char.IsWhiteSpace(value[size - 1])) {
			--size;
		}
		value = value.Substring(0, size);
		var clipped = new List<Tag>();
		foreach (var tag in tags) {
			var from = Math.Min(tag.Offset, size);
			var till = Math.Min(tag.Offset + tag.Length, size);
			// Tags never cover the line break that ends a block.
			while (till > from && value[till - 1] == '\n') {
				--till;
			}
			if (till > from) {
				clipped.Add(new Tag(from, till - from, tag.Id));
			}
		}
		// Code and pre are not combined with other formatting in Telegram.
		var canonical = Canonical(clipped, size);
		foreach (var tag in canonical) {
			var parts = SplitTags(tag.Id);
			var pre = parts.FirstOrDefault(p => p.StartsWith(Pre, StringComparison.Ordinal));
			if (pre != null) {
				tag.Id = JoinTags(parts.Where(p => p == pre || p == Quote || p == QuoteCollapsed));
			} else if (parts.Contains(Code)) {
				tag.Id = JoinTags(parts.Where(p => p == Code || p == Quote || p == QuoteCollapsed));
			}
		}
		return new Tagged(value, Canonical(canonical, size));
	}

	// Few-shot: small fast models copy the look of the examples much better
	// than they follow abstract rules.
	public static string BuildPrompt(string html, string extra, bool bolder) {
		var result = new StringBuilder();
		result.Append("You are a top Telegram channel editor. Turn the author's message into a creative, aesthetic, eye-catching Telegram message that still sounds like the author.\n\n");
		result.Append("Style:\n");
		result.Append("- Open with a hook: the main idea in <b>bold</b> with one fitting emoji at the start of the first line.\n");
		result.Append("- Break the text into short airy blocks with an empty line (<br><br>) between them.\n");
		result.Append("- <b>Bold</b> the key words, times, dates, prices, names; <i>italic</i> for tone and asides; <u>underline</u> for a must-not-miss detail.\n");
		result.Append("- Two or more parallel points become a list: one point per line, each starting with a fitting emoji (✅ \U0001F539 ▫️ \U0001F4CC ⚡ and the like), no <ul>/<li>.\n");
		result.Append("- The strongest phrase or a quote goes into <blockquote>...</blockquote>; a joke, punchline or surprise into <tg-spoiler>...</tg-spoiler>.\n");
		result.Append("- <code>...</code> only for things to copy: commands, codes, numbers, addresses. Keep existing links as <a href=\"...\">text</a>.\n");
		result.Append("- 2-6 emoji in total, placed meaningfully, never at the end of every line. A very short message gets one emoji and one bold accent, no list, no quote.\n");
		result.Append("- Never wrap the whole message in one style; formatting must make it easier to read, not louder.\n\n");
		result.Append("Content rules:\n");
		result.Append("- Same language, voice, slang and profanity as the author. You may reorder and lightly rephrase for rhythm, fix typos and punctuation.\n");
		result.Append("- Never invent facts, numbers, names, links, greetings, signatures, hashtags or calls to action that are not in the message.\n");
		result.Append("- Tokens like ⟦1⟧ are custom emoji or mentions: copy each exactly once, unchanged, near its original place.\n");
		result.Append("- Output Telegram HTML only: <b> <i> <u> <s> <tg-spoiler> <code> <pre> <blockquote> <a href> <br>. Line breaks only as <br>.\n");
		result.Append("- Answer with the formatted message only: no explanations, no ``` fences, no <<< >>> markers. Do not run any commands or tools.\n\n");
		result.Append("Example 1\nMessage:\n<<<\nребята завтра созвон в 19:00 по проекту, надо обсудить дизайн новый бюджет и сроки, ссылка будет в чате не опаздывайте пж\n>>>\nAnswer:\n");
		result.Append("\U0001F4C5 <b>Завтра созвон по проекту</b> в <b>19:00</b><br><br>Обсуждаем:<br>\U0001F3A8 новый дизайн<br>\U0001F4B0 бюджет<br>⏳ сроки<br><br>\U0001F517 Ссылка будет в чате<br><blockquote>Не опаздывайте, пж \U0001F64F</blockquote>\n\n");
		result.Append("Example 2\nMessage:\n<<<\nкороче я вчера наконец доделал бота, теперь он сам режет видео в кружки за 10 секунд. осталось только починить звук, но это мелочи\n>>>\nAnswer:\n");
		result.Append("\U0001F680 <b>Короче, я наконец доделал бота!</b><br><br>Теперь он сам режет видео в кружки <b>за 10 секунд</b> ⚡<br><br>Осталось только починить звук, <i>но это мелочи</i> <tg-spoiler>(наверное \U0001F605)</tg-spoiler>\n\n");
		result.Append("Example 3\nMessage:\n<<<\nок давай\n>>>\nAnswer:\n\U0001F44C <b>Ок, давай!</b>\n\n");
		if (bolder) {
			result.Append("Your previous answer was too plain. Be bolder and more creative this time: clear hook, structure, emoji, at least three kinds of formatting where the text allows it.\n\n");
		}
		if (!string.IsNullOrWhiteSpace(extra)) {
			result.Append("Author's own wishes for the style: " + extra.Trim() + "\n\n");
		}
		result.Append("Now the real message.\nMessage:\n<<<\n" + html + "\n>>>\nAnswer:\n");
		return result.ToString();
	}

	public static string BuildPrompt(string html, string extra) {
		return BuildPrompt(html, extra, false);
	}

	// Clean mode: a careful copy editor, not a designer.
	public static string BuildCleanPrompt(string html, string extra) {
		var result = new StringBuilder();
		result.Append("You are a careful copy editor for Telegram messages. Return the same message, cleaned up:\n");
		result.Append("- Correct capitalization and punctuation: commas, periods, question marks, dashes, «» quotes for Russian. Fix obvious typos.\n");
		result.Append("- Readable whitespace: split a long message into short paragraphs with an empty line (<br><br>) between them.\n");
		result.Append("- <i>Italics</i> sparingly, only where emphasis, a term, a title or an aside really fits.\n");
		result.Append("- Only a long message with clearly different parts gets headings: <h1>, <h2>, <h3> on their own lines.\n");
		result.Append("- No bold, no emoji, no lists unless the author already enumerates, no underline, no spoilers.\n");
		result.Append("- Keep every word, the order, the author's voice, slang and profanity. Do not rephrase, shorten, add or translate anything.\n");
		result.Append("- Keep links as <a href=\"...\">text</a>, commands and codes as <code>...</code>. Tokens like \u27E61\u27E7 are custom emoji or mentions: copy each unchanged.\n");
		result.Append("- Output Telegram HTML only (<i> <h1> <h2> <h3> <code> <a> <br>), the message only, no explanations, no ``` fences. Do not run any commands or tools.\n");
		if (!string.IsNullOrWhiteSpace(extra)) {
			result.Append("- Author's own wishes: " + extra.Trim() + "\n");
		}
		result.Append("\nExample\nMessage:\n<<<\nкороче я вчера доделал бота он теперь сам режет видео в кружки осталось звук починить но это мелочи\n>>>\nAnswer:\nКороче, я вчера доделал бота: он теперь сам режет видео в кружки.<br><br>Осталось звук починить, <i>но это мелочи</i>.\n\n");
		result.Append("Now the real message.\nMessage:\n<<<\n" + html + "\n>>>\nAnswer:\n");
		return result.ToString();
	}

	static bool IsEmojiCode(int code) {
		return (code >= 0x1F300 && code <= 0x1FAFF) || (code >= 0x2600 && code <= 0x27BF)
			|| (code >= 0x2B00 && code <= 0x2BFF) || code == 0xFE0F || code == 0x200D
			|| (code >= 0x1F1E6 && code <= 0x1F1FF);
	}

	// Clean mode guarantees: no bold and no emoji the author did not type,
	// whatever the model did. Tags follow the removed characters.
	public static Tagged Enforce(Tagged original, Tagged result) {
		var allowed = new HashSet<int>();
		for (var i = 0; i < original.Text.Length; ++i) {
			var code = char.IsSurrogatePair(original.Text, i) ? char.ConvertToUtf32(original.Text, i) : original.Text[i];
			if (IsEmojiCode(code)) allowed.Add(code);
			if (code > 0xFFFF) ++i;
		}
		var text = result.Text;
		var map = new int[text.Length + 1];
		var output = new StringBuilder();
		for (var i = 0; i < text.Length; ) {
			var pair = char.IsSurrogatePair(text, i);
			var code = pair ? char.ConvertToUtf32(text, i) : text[i];
			var width = pair ? 2 : 1;
			var drop = IsEmojiCode(code) && !allowed.Contains(code);
			if (drop) {
				// The space before a removed emoji goes too when punctuation,
				// a space or the line end follows.
				var next = i + width < text.Length ? text[i + width] : '\n';
				if (output.Length > 0 && output[output.Length - 1] == ' '
					&& (char.IsPunctuation(next) || char.IsWhiteSpace(next))) {
					output.Length -= 1;
				}
			}
			for (var k = 0; k < width; ++k) map[i + k] = output.Length;
			if (!drop) output.Append(text, i, width);
			i += width;
		}
		map[text.Length] = output.Length;
		var cleaned = Regex.Replace(output.ToString(), "[ \\t]{2,}", " ");
		if (cleaned.Length != output.Length) {
			// Rare: collapsing spaces after a removed emoji; keep tags simple.
			cleaned = output.ToString();
		}
		var tags = new List<Tag>();
		foreach (var tag in result.Tags) {
			var parts = SplitTags(tag.Id).Where(p => p != Bold).ToList();
			if (parts.Count == 0) continue;
			var from = map[Math.Min(tag.Offset, text.Length)];
			var till = map[Math.Min(tag.Offset + tag.Length, text.Length)];
			if (till > from) tags.Add(new Tag(from, till - from, JoinTags(parts)));
		}
		var value = cleaned;
		var trimmed = value.TrimEnd();
		return new Tagged(trimmed, Canonical(tags.Where(t => t.Offset < trimmed.Length).Select(t => new Tag(t.Offset, Math.Min(t.Length, trimmed.Length - t.Offset), t.Id)).ToList(), trimmed.Length));
	}

	static int CountEmoji(string text) {
		var result = 0;
		for (var i = 0; i < text.Length; ++i) {
			var code = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
			if ((code >= 0x1F300 && code <= 0x1FAFF) || (code >= 0x2600 && code <= 0x27BF) || (code >= 0x2B00 && code <= 0x2BFF) || code == 0x2328 || code == 0x23F3 || code == 0x231B) {
				++result;
			}
			if (code > 0xFFFF) ++i;
		}
		return result;
	}

	// How much the answer is really formatted: kinds of formatting, emoji,
	// structure. A whole message in one style counts as plain.
	public static int Score(Tagged original, Tagged result) {
		var size = Math.Max(1, result.Text.Length);
		var kinds = new HashSet<string>();
		var covered = new bool[result.Text.Length];
		foreach (var tag in result.Tags) {
			foreach (var part in SplitTags(tag.Id)) {
				if (IsProtectedPart(part)) continue;
				kinds.Add(part.StartsWith(Pre, StringComparison.Ordinal) ? Pre : IsLinkPart(part) ? "link" : part);
				for (var k = tag.Offset; k < Math.Min(tag.Offset + tag.Length, covered.Length); ++k) covered[k] = true;
			}
		}
		var coverage = covered.Count(c => c) / (double)size;
		var score = kinds.Count * 2;
		score += Math.Min(4, Math.Max(0, CountEmoji(result.Text) - CountEmoji(original.Text)));
		var lines = result.Text.Count(c => c == '\n') - original.Text.Count(c => c == '\n');
		if (lines > 0) score += 2;
		if (coverage > 0.9) score -= 4;
		return score;
	}

	// The bar a good answer should pass, lower for short messages.
	public static int GoodScore(Tagged original) {
		var visible = CountVisible(original.Text);
		return visible < 25 ? 3 : visible < 80 ? 5 : 7;
	}

	public static bool LooksSane(Tagged original, Tagged result) {
		var was = CountVisible(original.Text);
		var now = CountVisible(result.Text);
		if (now == 0) return false;
		if (was >= 20 && now * 2 < was) return false;
		return now <= was * 3 + 300;
	}

	// TextUtilities::SerializeTags: QDataStream Qt_5_1, big endian.
	public static byte[] SerializeTags(List<Tag> tags) {
		if (tags.Count == 0) {
			return new byte[0];
		}
		var stream = new MemoryStream();
		Action<int> writeInt = (value) => {
			stream.WriteByte((byte)((value >> 24) & 0xFF));
			stream.WriteByte((byte)((value >> 16) & 0xFF));
			stream.WriteByte((byte)((value >> 8) & 0xFF));
			stream.WriteByte((byte)(value & 0xFF));
		};
		writeInt(tags.Count);
		foreach (var tag in tags) {
			writeInt(tag.Offset);
			writeInt(tag.Length);
			if (tag.Id == null) {
				writeInt(-1);
			} else {
				var bytes = Encoding.BigEndianUnicode.GetBytes(tag.Id);
				writeInt(bytes.Length);
				stream.Write(bytes, 0, bytes.Length);
			}
		}
		return stream.ToArray();
	}

	// TextUtilities::DeserializeTags with the same checks.
	public static List<Tag> DeserializeTags(byte[] data, int textLength) {
		var result = new List<Tag>();
		if (data == null || data.Length < 4) {
			return result;
		}
		var position = 0;
		Func<int?> readInt = () => {
			if (position + 4 > data.Length) return null;
			var value = (data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
			position += 4;
			return value;
		};
		var count = readInt();
		if (count == null || count <= 0 || count > textLength) {
			return result;
		}
		for (var i = 0; i != count; ++i) {
			var offset = readInt();
			var length = readInt();
			var bytes = readInt();
			if (offset == null || length == null || bytes == null) return result;
			var id = "";
			if (bytes.Value != -1) {
				if (bytes.Value < 0 || position + bytes.Value > data.Length) return result;
				id = Encoding.BigEndianUnicode.GetString(data, position, bytes.Value);
				position += bytes.Value;
			}
			if (offset < 0 || length <= 0 || offset + length > textLength) {
				return result;
			}
			result.Add(new Tag(offset.Value, length.Value, id));
		}
		return result;
	}

	// CF_HTML for other programs; Telegram takes its own formats first.
	public static byte[] CfHtml(string fragment) {
		const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
		const string before = "<html><body>\r\n<!--StartFragment-->";
		const string after = "<!--EndFragment-->\r\n</body></html>";
		var utf8 = new UTF8Encoding(false);
		var headerSize = utf8.GetByteCount(string.Format(header, 0, 0, 0, 0));
		var startFragment = headerSize + utf8.GetByteCount(before);
		var endFragment = startFragment + utf8.GetByteCount(fragment);
		var endHtml = endFragment + utf8.GetByteCount(after);
		var full = string.Format(header, headerSize, endHtml, startFragment, endFragment) + before + fragment + after;
		return utf8.GetBytes(full);
	}
}

public class Config {
	public bool Enabled = true;
	public string Engine = "auto";
	public string CodexPath = "";
	public string CodexModel = "gpt-6.1-terra";
	public string CodexEffort = "medium";
	public bool CodexUserConfig = false;
	public string Key = "";
	public string BaseUrl = "https://api.groq.com/openai/v1";
	public string Model = "openai/gpt-oss-120b";
	public string Style = "";
	// "clean": capitals, punctuation, italics, paragraphs and headings, no bold
	// and no emoji (default); "fancy": the creative channel look.
	public string Mode = "clean";
	// Enter in AyuGram formats the message first, then sends it.
	public bool OnEnter = true;
	public int EnterTimeout = 25;
	public bool Button = true;
	// Button position from the bottom right corner of the AyuGram window,
	// in pixels at 100% scale: just above the send button by default.
	public int ButtonRight = 14;
	public int ButtonBottom = 62;
	public List<string> Apps = new List<string> { "ayugram", "telegram", "64gram", "kotatogram", "materialgram", "exteragram" };

	public static string Folder() {
		var custom = Environment.GetEnvironmentVariable("AYU_FANCY_HOME");
		if (!string.IsNullOrEmpty(custom)) return custom;
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AyuFancy");
	}

	static string Str(Dictionary<string, object> json, string name, string fallback) {
		object value;
		if (json.TryGetValue(name, out value) && value is string) {
			return ((string)value).Trim();
		}
		return fallback;
	}

	static bool Bool(Dictionary<string, object> json, string name, bool fallback) {
		object value;
		if (json.TryGetValue(name, out value) && value is bool) {
			return (bool)value;
		}
		return fallback;
	}

	public static Config Load() {
		var result = new Config();
		var path = Path.Combine(Folder(), "ayu_fancy.json");
		try {
			if (File.Exists(path)) {
				var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF')) as Dictionary<string, object>;
				if (json != null) {
					result.Enabled = Bool(json, "enabled", true);
					result.Engine = Str(json, "engine", result.Engine).ToLowerInvariant();
					result.CodexPath = Str(json, "codex_path", result.CodexPath);
					result.CodexModel = Str(json, "codex_model", result.CodexModel);
					result.CodexEffort = Str(json, "codex_effort", result.CodexEffort);
					result.CodexUserConfig = Bool(json, "codex_user_config", false);
					result.Key = Str(json, "key", result.Key);
					result.BaseUrl = Str(json, "base_url", result.BaseUrl).TrimEnd('/');
					result.Model = Str(json, "model", result.Model);
					result.Style = Str(json, "style", result.Style);
					result.Button = Bool(json, "button", true);
					result.Mode = Str(json, "mode", result.Mode).ToLowerInvariant();
					result.OnEnter = Bool(json, "on_enter", true);
					object timeout;
					if (json.TryGetValue("enter_timeout", out timeout) && timeout is int) result.EnterTimeout = Math.Max(3, (int)timeout);
					object number;
					if (json.TryGetValue("button_right", out number) && number is int) result.ButtonRight = (int)number;
					if (json.TryGetValue("button_bottom", out number) && number is int) result.ButtonBottom = (int)number;
					object apps;
					if (json.TryGetValue("apps", out apps) && apps is object[]) {
						result.Apps = ((object[])apps).OfType<string>().Select(a => a.ToLowerInvariant()).ToList();
					}
				}
			}
		} catch (Exception e) {
			Log.Write("bad config: " + e.Message);
		}
		if (string.IsNullOrEmpty(result.Key)) {
			result.Key = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
		}
		return result;
	}
}

public static class Log {
	static readonly object Lock = new object();

	public static void Write(string text) {
		try {
			lock (Lock) {
				var path = Path.Combine(Config.Folder(), "ayu_fancy.log");
				Directory.CreateDirectory(Config.Folder());
				if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) {
					File.Delete(path);
				}
				File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + "\r\n", Encoding.UTF8);
			}
		} catch {
		}
	}
}

public class Answer {
	public string Text = "";
	public string Error = "";
	public string Engine = "";
}

public static class Engines {
	public static string Quote(string arg) {
		if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) {
			return arg;
		}
		return "\"" + arg.Replace("\"", "\\\"") + "\"";
	}

	static string NpmCodexExe(string shimDir) {
		var roots = new[] {
			Path.Combine(shimDir, "node_modules\\@openai\\codex\\node_modules\\@openai"),
			Path.Combine(shimDir, "node_modules\\@openai"),
		};
		foreach (var root in roots) {
			foreach (var tail in new[] {
					"codex-win32-x64\\vendor\\x86_64-pc-windows-msvc\\bin\\codex.exe",
					"codex-win32-x64\\vendor\\x86_64-pc-windows-msvc\\codex\\codex.exe",
					"codex-win32-arm64\\vendor\\aarch64-pc-windows-msvc\\bin\\codex.exe" }) {
				var path = Path.Combine(root, tail);
				if (File.Exists(path)) return path;
			}
		}
		return null;
	}

	public static string FindCodex(Config config) {
		if (!string.IsNullOrEmpty(config.CodexPath)) {
			return File.Exists(config.CodexPath) ? config.CodexPath : null;
		}
		var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
		foreach (var dir in paths) {
			if (string.IsNullOrWhiteSpace(dir)) continue;
			try {
				var exe = Path.Combine(dir.Trim(), "codex.exe");
				if (File.Exists(exe)) return exe;
				var shim = Path.Combine(dir.Trim(), "codex.cmd");
				if (File.Exists(shim)) {
					var real = NpmCodexExe(dir.Trim());
					if (real != null) return real;
				}
			} catch {
			}
		}
		var portable = Path.Combine(Config.Folder(), "codex\\package\\vendor\\x86_64-pc-windows-msvc\\bin\\codex.exe");
		return File.Exists(portable) ? portable : null;
	}

	public static Answer AskCodex(Config config, string codex, string prompt) {
		var answer = new Answer { Engine = "Codex" };
		var dir = Path.Combine(Path.GetTempPath(), "ayufancy-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var output = Path.Combine(dir, "answer.txt");
		var args = new List<string> {
			"exec", "--skip-git-repo-check", "--ephemeral", "--ignore-rules",
			"--sandbox", "read-only", "--color", "never",
			"-C", dir, "-o", output,
		};
		if (!config.CodexUserConfig) args.Add("--ignore-user-config");
		// Every tool description goes to the model with each request:
		// formatting text needs none of them, without them it answers faster.
		foreach (var feature in new[] {
				"apps", "browser_use", "browser_use_external", "computer_use",
				"image_generation", "multi_agent", "plugins", "remote_plugin",
				"shell_tool", "unified_exec", "view_image", "skill_search",
				"tool_suggest", "sleep_tool", "goals", "hooks", "in_app_browser",
				"realtime_conversation", "worktrees", "workspace_dependencies",
				"unbounded_connection_retries" }) {
			args.Add("--disable");
			args.Add(feature);
		}
		if (!string.IsNullOrEmpty(config.CodexEffort)) {
			args.Add("-c");
			args.Add("model_reasoning_effort=" + config.CodexEffort);
		}
		if (!string.IsNullOrEmpty(config.CodexModel)) {
			args.Add("-m");
			args.Add(config.CodexModel);
		}
		args.Add("-");
		var info = new ProcessStartInfo {
			FileName = codex,
			Arguments = string.Join(" ", args.Select(a => Quote(a))),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardErrorEncoding = Encoding.UTF8,
			StandardOutputEncoding = Encoding.UTF8,
			WorkingDirectory = dir,
		};
		var errors = new StringBuilder();
		string abort = null;
		try {
			using (var process = new Process { StartInfo = info }) {
				process.ErrorDataReceived += (sender, e) => {
					if (e.Data == null) return;
					string text;
					lock (errors) {
						errors.AppendLine(e.Data);
						text = errors.ToString();
					}
					if (abort != null) return;
					if (Regex.IsMatch(text, "(?<![\\d.:-])401(?!\\d)") || text.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0) {
						abort = "Codex не вошёл в аккаунт, запусти AYU_FANCY.cmd";
					} else if (text.IndexOf("waiting for network", StringComparison.OrdinalIgnoreCase) >= 0) {
						abort = "Codex: нет связи с OpenAI";
					}
					if (abort != null) {
						try { process.Kill(); } catch { }
					}
				};
				process.OutputDataReceived += (sender, e) => { };
				process.Start();
				process.BeginErrorReadLine();
				process.BeginOutputReadLine();
				var bytes = new UTF8Encoding(false).GetBytes(prompt);
				process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
				process.StandardInput.BaseStream.Flush();
				process.StandardInput.Close();
				if (!process.WaitForExit(45000)) {
					try { process.Kill(); } catch { }
					answer.Error = "Codex не ответил за 45 с";
				} else {
					process.WaitForExit();
					if (abort != null) {
						answer.Error = abort;
					} else if (File.Exists(output)) {
						answer.Text = File.ReadAllText(output, Encoding.UTF8).Trim();
					}
					if (answer.Error.Length == 0 && answer.Text.Length == 0) {
						var lines = errors.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
						var line = lines.LastOrDefault(l => l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) ?? lines.LastOrDefault() ?? "";
						answer.Error = process.ExitCode == 0
							? "Codex вернул пустой ответ"
							: "Codex: " + (line.Length > 0 ? (line.Length > 200 ? line.Substring(0, 200) : line) : ("код " + process.ExitCode));
					}
				}
			}
		} catch (Exception e) {
			answer.Error = "Codex не запустился: " + e.Message;
		}
		Log.Write("codex: " + (answer.Error.Length > 0 ? answer.Error : ("ok " + answer.Text.Length + " chars")));
		try { Directory.Delete(dir, true); } catch { }
		return answer;
	}

	public static Answer AskChat(Config config, string model, string prompt) {
		var answer = new Answer { Engine = "Groq" };
		var status = 0;
		var message = "";
		try {
			ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
			var request = (HttpWebRequest)WebRequest.Create(config.BaseUrl + "/chat/completions");
			request.Method = "POST";
			request.ContentType = "application/json";
			request.Headers["Authorization"] = "Bearer " + config.Key;
			request.Timeout = 90000;
			request.ReadWriteTimeout = 90000;
			var body = new Dictionary<string, object> {
				{ "model", model },
				{ "temperature", 0.4 },
				{ "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } } },
			};
			if (model.Contains("gpt-oss")) body["reasoning_effort"] = "low";
			var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
			using (var stream = request.GetRequestStream()) {
				stream.Write(bytes, 0, bytes.Length);
			}
			string text;
			try {
				using (var response = (HttpWebResponse)request.GetResponse())
				using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
					status = (int)response.StatusCode;
					text = reader.ReadToEnd();
				}
			} catch (WebException e) {
				var response = e.Response as HttpWebResponse;
				if (response == null) throw;
				status = (int)response.StatusCode;
				using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
					text = reader.ReadToEnd();
				}
			}
			var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
			if (status == 200 && json != null && json.ContainsKey("choices")) {
				var choice = ((object[])json["choices"])[0] as Dictionary<string, object>;
				var content = (choice["message"] as Dictionary<string, object>)["content"] as string;
				if (!string.IsNullOrWhiteSpace(content)) {
					answer.Text = content.Trim();
					return answer;
				}
			}
			if (json != null && json.ContainsKey("error")) {
				var error = json["error"] as Dictionary<string, object>;
				if (error != null && error.ContainsKey("message")) message = error["message"] as string ?? "";
			}
		} catch (Exception e) {
			message = e.Message;
		}
		Log.Write("chat " + model + " " + status + ": " + message);
		if ((status == 404 || status == 400) && message.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 && model != "llama-3.3-70b-versatile") {
			return AskChat(config, "llama-3.3-70b-versatile", prompt);
		}
		answer.Error = status == 401 ? "Groq: ключ не подходит" : status != 0 ? ("Groq: ошибка " + status) : "Groq: нет сети";
		return answer;
	}

	// Codex and Groq race when both are there: the first good answer wins,
	// Groq usually answers in about a second, Codex is the backup.
	public static Answer Ask(Config config, string prompt) {
		var codex = config.Engine != "groq" ? FindCodex(config) : null;
		var chat = config.Engine != "codex" && !string.IsNullOrEmpty(config.Key);
		if (codex == null && !chat) {
			return new Answer { Error = config.Engine == "codex" ? "Codex не найден, запусти AYU_FANCY.cmd" : "Нет ни Codex, ни ключа Groq, запусти AYU_FANCY.cmd" };
		}
		if (codex == null) return AskChat(config, config.Model, prompt);
		if (!chat) return AskCodex(config, codex, prompt);

		var gate = new object();
		Answer winner = null;
		var errors = new List<string>();
		var pending = 2;
		var done = new ManualResetEvent(false);
		Action<Func<Answer>> start = (ask) => {
			var thread = new Thread(() => {
				Answer answer;
				try {
					answer = ask();
				} catch (Exception e) {
					answer = new Answer { Error = e.Message };
				}
				lock (gate) {
					--pending;
					if (winner == null) {
						if (answer.Error.Length == 0 && answer.Text.Length > 0) {
							winner = answer;
							done.Set();
						} else {
							errors.Add(answer.Error);
						}
					}
					if (pending == 0) done.Set();
				}
			});
			thread.IsBackground = true;
			thread.Start();
		};
		start(() => AskChat(config, config.Model, prompt));
		start(() => AskCodex(config, codex, prompt));
		done.WaitOne();
		lock (gate) {
			return winner ?? new Answer { Error = string.Join("; ", errors.Where(e => e.Length > 0)) };
		}
	}

	public class Result {
		public Tagged Text;
		public string Engine = "";
		public string Error = "";
		public int Lost;
		public string Raw = "";
		public long Milliseconds;
	}

	// Whole pipeline for one piece of field text.
	public static Result FormatClean(Config config, Tagged source) {
		var result = new Result();
		var watch = Stopwatch.StartNew();
		var items = new List<Protected>();
		var prepared = Core.Protect(source, items);
		var prompt = Core.BuildCleanPrompt(Core.ToHtml(prepared), config.Style);
		Tagged best = null;
		var errors = new List<string>();
		// The chosen Codex model answers; Groq only when Codex is missing or failed.
		var answers = new List<Answer>();
		var codex = config.Engine != "groq" ? FindCodex(config) : null;
		if (codex != null) answers.Add(AskCodex(config, codex, prompt));
		if ((codex == null || answers[0].Error.Length > 0) && config.Engine != "codex" && !string.IsNullOrEmpty(config.Key)) {
			answers.Add(AskChat(config, config.Model, prompt));
		}
		if (answers.Count == 0) answers.Add(new Answer { Error = "Нет ни Codex, ни ключа Groq, запусти AYU_FANCY.cmd" });
		foreach (var answer in answers) {
			if (answer.Error.Length > 0) { errors.Add(answer.Error); continue; }
			int lost;
			// A heading gets an empty line under it, it can't be bold here.
			var html = Regex.Replace(Core.CleanAnswer(answer.Text), "(</h[1-6]>)(?!\\s*<br)", "$1<br>", RegexOptions.IgnoreCase);
			var restored = Core.Restore(Core.FromHtml(html, false), items, out lost);
			restored = Core.Enforce(source, restored);
			if (!Core.LooksSane(source, restored)) { errors.Add(answer.Engine + " вернул что-то не то"); continue; }
			best = restored;
			result.Engine = answer.Engine;
			result.Raw = answer.Text;
			result.Lost = lost;
			break;
		}
		result.Milliseconds = watch.ElapsedMilliseconds;
		if (best == null) {
			result.Error = errors.Count > 0 ? string.Join("; ", errors.Distinct()) : "нет ответа";
			return result;
		}
		result.Text = best;
		return result;
	}

	class Candidate {
		public Answer Answer;
		public Tagged Text;
		public int Lost;
		public int Score;
	}

	// Asks every engine there is, waits a little for the slower one after
	// the first good answer, returns all answers that came.
	public static List<Answer> AskAll(Config config, string prompt, Func<Answer, bool> good) {
		var codex = config.Engine != "groq" ? FindCodex(config) : null;
		var chat = config.Engine != "codex" && !string.IsNullOrEmpty(config.Key);
		var asks = new List<Func<Answer>>();
		if (chat) asks.Add(() => AskChat(config, config.Model, prompt));
		if (codex != null) asks.Add(() => AskCodex(config, codex, prompt));
		var answers = new List<Answer>();
		if (asks.Count == 0) {
			answers.Add(new Answer { Error = config.Engine == "codex" ? "Codex не найден, запусти AYU_FANCY.cmd" : "Нет ни Codex, ни ключа Groq, запусти AYU_FANCY.cmd" });
			return answers;
		}
		var gate = new object();
		var pending = asks.Count;
		var firstGood = new ManualResetEvent(false);
		var all = new ManualResetEvent(false);
		foreach (var ask in asks) {
			var call = ask;
			var thread = new Thread(() => {
				Answer answer;
				try { answer = call(); } catch (Exception e) { answer = new Answer { Error = e.Message }; }
				lock (gate) {
					answers.Add(answer);
					if (answer.Error.Length == 0 && answer.Text.Length > 0 && good(answer)) firstGood.Set();
					if (--pending == 0) { firstGood.Set(); all.Set(); }
				}
			});
			thread.IsBackground = true;
			thread.Start();
		}
		// A good answer goes in at once, the slower engine is only waited for
		// when everything that came so far is weak.
		firstGood.WaitOne();
		lock (gate) {
			return answers.ToList();
		}
	}

	public static Result Format(Config config, Tagged source) {
		if (config.Mode != "fancy") {
			return FormatClean(config, source);
		}
		var result = new Result();
		var watch = Stopwatch.StartNew();
		var items = new List<Protected>();
		var prepared = Core.Protect(source, items);
		var html = Core.ToHtml(prepared);
		var good = Core.GoodScore(source);
		Candidate best = null;
		var errors = new List<string>();
		for (var attempt = 0; attempt < 2; ++attempt) {
			Func<Answer, Candidate> judge = (answer) => {
				int lost;
				var restored = Core.Restore(Core.FromHtml(Core.CleanAnswer(answer.Text)), items, out lost);
				if (!Core.LooksSane(source, restored)) return null;
				return new Candidate { Answer = answer, Text = restored, Lost = lost, Score = Core.Score(source, restored) - lost * 3 };
			};
			var answers = AskAll(config, Core.BuildPrompt(html, config.Style, attempt > 0), (answer) => {
				var candidate = judge(answer);
				return candidate != null && candidate.Score >= good;
			});
			foreach (var answer in answers) {
				if (answer.Error.Length > 0) {
					errors.Add(answer.Error);
					continue;
				}
				int lost;
				var restored = Core.Restore(Core.FromHtml(Core.CleanAnswer(answer.Text)), items, out lost);
				if (!Core.LooksSane(source, restored)) {
					errors.Add(answer.Engine + " вернул что-то не то");
					continue;
				}
				var candidate = new Candidate { Answer = answer, Text = restored, Lost = lost, Score = Core.Score(source, restored) - lost * 3 };
				Log.Write(answer.Engine + " score " + candidate.Score + " (good " + good + ")");
				if (best == null || candidate.Score > best.Score) best = candidate;
			}
			// One more try only when everything was plain and there is time.
			if (best != null && (best.Score >= good || watch.ElapsedMilliseconds > 3000)) break;
			if (best == null && errors.Count > 0 && attempt == 0 && errors.All(e => e.Contains("не найден") || e.Contains("Нет ни"))) break;
		}
		result.Milliseconds = watch.ElapsedMilliseconds;
		if (best == null) {
			result.Error = errors.Count > 0 ? string.Join("; ", errors.Distinct()) : "нет ответа";
			return result;
		}
		result.Engine = best.Answer.Engine;
		result.Raw = best.Answer.Text;
		result.Text = best.Text;
		result.Lost = best.Lost;
		return result;
	}
}

// Small status line over the AyuGram window, never takes the focus.
public class Osd : Form {
	readonly Label _label;
	readonly System.Windows.Forms.Timer _timer;

	public Osd() {
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		TopMost = true;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.FromArgb(36, 32, 48);
		Padding = new Padding(14, 8, 14, 8);
		AutoSize = true;
		AutoSizeMode = AutoSizeMode.GrowAndShrink;
		_label = new Label {
			AutoSize = true,
			ForeColor = Color.White,
			Font = new Font("Segoe UI", 10.5f),
			MaximumSize = new Size(560, 0),
		};
		Controls.Add(_label);
		_timer = new System.Windows.Forms.Timer();
		_timer.Tick += (sender, e) => { _timer.Stop(); Hide(); };
	}

	protected override bool ShowWithoutActivation {
		get { return true; }
	}

	protected override CreateParams CreateParams {
		get {
			var result = base.CreateParams;
			result.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE, TOOLWINDOW, TOPMOST
			return result;
		}
	}

	public void ShowText(string text, IntPtr window, int milliseconds) {
		_label.Text = text;
		PerformLayout();
		var area = Screen.FromPoint(Cursor.Position).WorkingArea;
		Native.RECT rect;
		if (window != IntPtr.Zero && Native.GetWindowRect(window, out rect)) {
			area = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
		}
		Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 90);
		if (!Visible) Show();
		_timer.Stop();
		if (milliseconds > 0) {
			_timer.Interval = milliseconds;
			_timer.Start();
		}
	}
}

public static class Native {
	[StructLayout(LayoutKind.Sequential)]
	public struct RECT {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
	[DllImport("user32.dll")]
	public static extern bool UnhookWindowsHookEx(IntPtr hook);
	[DllImport("user32.dll")]
	public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
	[DllImport("kernel32.dll")]
	public static extern IntPtr GetModuleHandle(string name);
	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int key);
	[DllImport("user32.dll")]
	public static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr window);
	[DllImport("user32.dll")]
	public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
	[DllImport("user32.dll")]
	public static extern bool GetWindowRect(IntPtr window, out RECT rect);
	[DllImport("user32.dll")]
	public static extern bool GetClientRect(IntPtr window, out RECT rect);
	[DllImport("user32.dll")]
	public static extern bool ClientToScreen(IntPtr window, ref Point point);
	[DllImport("user32.dll")]
	public static extern bool IsIconic(IntPtr window);
	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(IntPtr window);
	[DllImport("user32.dll")]
	public static extern IntPtr GetAncestor(IntPtr window, uint flags);
	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(IntPtr window);
	[DllImport("user32.dll")]
	public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
	[DllImport("user32.dll")]
	public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

	public const int VK_SHIFT = 0x10;
	public const int VK_CONTROL = 0x11;
	public const int VK_MENU = 0x12;
	public const int VK_LWIN = 0x5B;
	public const int VK_RWIN = 0x5C;
	public const int VK_F = 0x46;
	public const int VK_RETURN = 0x0D;
	public const int VK_ESCAPE = 0x1B;

	public static void Tap(int key) {
		keybd_event((byte)key, 0, 0, UIntPtr.Zero);
		keybd_event((byte)key, 0, 2, UIntPtr.Zero);
	}

	public static bool Down(int key) {
		return (GetAsyncKeyState(key) & 0x8000) != 0;
	}

	public static void Press(int modifier, int key) {
		keybd_event((byte)modifier, 0, 0, UIntPtr.Zero);
		keybd_event((byte)key, 0, 0, UIntPtr.Zero);
		keybd_event((byte)key, 0, 2, UIntPtr.Zero);
		keybd_event((byte)modifier, 0, 2, UIntPtr.Zero);
	}
}

// Round ✨ button glued to the AyuGram window above the send button.
// Never takes the focus, so the message field stays active for the click.
// Right mouse button drags it, the place is remembered.
public class SparkButton : Form {
	public Action Clicked;
	public Action<int, int> Moved; // new offset from the bottom right, 100% scale
	bool _hover;
	bool _busy;
	bool _dragging;
	Point _dragFrom;
	float _scale = 1f;

	public SparkButton() {
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		TopMost = true;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.Magenta;
		TransparencyKey = Color.Magenta;
		DoubleBuffered = true;
		Cursor = Cursors.Hand;
		Size = new Size(38, 38);
		var tip = new ToolTip();
		tip.SetToolTip(this, "\u2728 Оформить красиво (Codex)  Ctrl+Shift+F\nПравой кнопкой можно перетащить");
	}

	protected override bool ShowWithoutActivation {
		get { return true; }
	}

	protected override CreateParams CreateParams {
		get {
			var result = base.CreateParams;
			result.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE, TOOLWINDOW, TOPMOST
			return result;
		}
	}

	protected override void WndProc(ref Message m) {
		const int WM_MOUSEACTIVATE = 0x21;
		const int MA_NOACTIVATE = 3;
		if (m.Msg == WM_MOUSEACTIVATE) {
			m.Result = (IntPtr)MA_NOACTIVATE;
			return;
		}
		base.WndProc(ref m);
	}

	public bool Busy {
		get { return _busy; }
		set { _busy = value; Invalidate(); }
	}

	public bool Dragging {
		get { return _dragging; }
	}

	public void Place(Rectangle client, float scale, int right, int bottom) {
		if (_dragging) return;
		_scale = scale;
		var size = (int)Math.Round(34 * scale);
		var location = new Point(
			client.Right - (int)Math.Round(right * scale) - size,
			client.Bottom - (int)Math.Round(bottom * scale) - size);
		if (Size.Width != size) Size = new Size(size, size);
		if (Location != location) Location = location;
	}

	protected override void OnPaint(PaintEventArgs e) {
		var g = e.Graphics;
		g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
		var color = _busy ? Color.FromArgb(110, 110, 130) : _hover ? Color.FromArgb(160, 105, 255) : Color.FromArgb(132, 82, 240);
		using (var brush = new SolidBrush(color)) {
			g.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
		}
		using (var font = new Font("Segoe UI Emoji", Height * 0.42f, GraphicsUnit.Pixel)) {
			var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
			g.DrawString(_busy ? "\u2026" : "\u2728", font, Brushes.White, new RectangleF(0, 1, Width - 1, Height - 1), format);
		}
	}

	protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

	protected override void OnMouseDown(MouseEventArgs e) {
		if (e.Button == MouseButtons.Right) {
			_dragging = true;
			_dragFrom = e.Location;
			Capture = true;
		}
		base.OnMouseDown(e);
	}

	protected override void OnMouseMove(MouseEventArgs e) {
		if (_dragging) {
			Location = new Point(Location.X + e.X - _dragFrom.X, Location.Y + e.Y - _dragFrom.Y);
		}
		base.OnMouseMove(e);
	}

	public Rectangle Client;

	protected override void OnMouseUp(MouseEventArgs e) {
		if (_dragging && e.Button == MouseButtons.Right) {
			_dragging = false;
			Capture = false;
			if (Moved != null && _scale > 0) {
				var right = (int)Math.Round((Client.Right - Right) / _scale);
				var bottom = (int)Math.Round((Client.Bottom - Bottom) / _scale);
				Moved(right, bottom);
			}
		} else if (e.Button == MouseButtons.Left && !_busy && Clicked != null) {
			Clicked();
		}
		base.OnMouseUp(e);
	}
}

public class TrayApp : ApplicationContext {
	readonly NotifyIcon _tray;
	readonly Osd _osd;
	readonly Native.HookProc _proc;
	readonly IntPtr _hook;
	readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();
	readonly SparkButton _button;
	readonly System.Windows.Forms.Timer _follow;
	IntPtr _target;
	Config _config;
	DateTime _configTime;
	int _busy;

	public TrayApp() {
		_osd = new Osd();
		_osd.CreateControl();
		if (_osd.Handle == IntPtr.Zero) Log.Write("no osd window"); // BeginInvoke target
		_tray = new NotifyIcon {
			Icon = MakeIcon(),
			Text = "AyuFancy: Ctrl+Shift+F в AyuGram",
			Visible = true,
			ContextMenuStrip = new ContextMenuStrip(),
		};
		_tray.ContextMenuStrip.Items.Add("\u2728 Кнопка у отправки или Ctrl+Shift+F в AyuGram").Enabled = false;
		var onEnter = new ToolStripMenuItem("Оформлять при отправке (Enter)") { CheckOnClick = true, Checked = Config.Load().OnEnter };
		onEnter.CheckedChanged += (s, e) => SaveSetting("on_enter", onEnter.Checked);
		_tray.ContextMenuStrip.Items.Add(onEnter);
		_tray.ContextMenuStrip.Items.Add("Настройки", null, (s, e) => OpenFile("ayu_fancy.json"));
		_tray.ContextMenuStrip.Items.Add("Лог", null, (s, e) => OpenFile("ayu_fancy.log"));
		_tray.ContextMenuStrip.Items.Add("Выход", null, (s, e) => ExitThread());
		_button = new SparkButton();
		_button.Clicked = () => StartRun(_target, Trigger.Button);
		_button.Moved = SaveButtonPlace;
		_follow = new System.Windows.Forms.Timer { Interval = 150 };
		_follow.Tick += (s, e) => Follow();
		_follow.Start();
		_proc = HookCallback;
		_hook = Native.SetWindowsHookEx(13, _proc, Native.GetModuleHandle(null), 0);
		Log.Write("started, hook " + (_hook != IntPtr.Zero ? "ok" : "FAILED " + Marshal.GetLastWin32Error()));
	}

	static Icon MakeIcon() {
		using (var bitmap = new Bitmap(32, 32)) {
			using (var g = Graphics.FromImage(bitmap)) {
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
				g.Clear(Color.Transparent);
				using (var brush = new SolidBrush(Color.FromArgb(150, 90, 255))) {
					g.FillEllipse(brush, 1, 1, 30, 30);
				}
				using (var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel)) {
					var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
					g.DrawString("F", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
				}
			}
			return Icon.FromHandle(bitmap.GetHicon());
		}
	}

	static void OpenFile(string name) {
		try {
			Process.Start("notepad.exe", Engines.Quote(Path.Combine(Config.Folder(), name)));
		} catch {
		}
	}

	protected override void ExitThreadCore() {
		if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
		_tray.Visible = false;
		_tray.Dispose();
		base.ExitThreadCore();
	}

	string ProcessName(IntPtr window) {
		uint pid;
		Native.GetWindowThreadProcessId(window, out pid);
		string name;
		if (!_names.TryGetValue(pid, out name)) {
			try {
				name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
			} catch {
				name = "";
			}
			_names[pid] = name;
		}
		return name;
	}

	IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam) {
		if (code >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104)) {
			var key = Marshal.ReadInt32(lParam);
			var flags = Marshal.ReadInt32(lParam, 8);
			var injected = (flags & 0x10) != 0;
			if (!injected && key == Native.VK_ESCAPE && _enterRun) {
				_sendAsIs.Set(); // Esc while formatting on Enter: send as typed
				return (IntPtr)1;
			}
			if (!injected
				&& key == Native.VK_RETURN
				&& !Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_CONTROL)
				&& !Native.Down(Native.VK_MENU)
				&& !Native.Down(Native.VK_LWIN)
				&& !Native.Down(Native.VK_RWIN)) {
				var window = Native.GetForegroundWindow();
				var config = CurrentConfig();
				if (config.Enabled && config.OnEnter && config.Apps.Contains(ProcessName(window))) {
					if (_busy == 0) StartRun(window, Trigger.Enter);
					return (IntPtr)1;
				}
			}
			if (!injected
				&& key == Native.VK_F
				&& Native.Down(Native.VK_CONTROL)
				&& Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_MENU)
				&& !Native.Down(Native.VK_LWIN)
				&& !Native.Down(Native.VK_RWIN)) {
				var window = Native.GetForegroundWindow();
				var name = ProcessName(window);
				var config = Config.Load();
				if (config.Enabled && config.Apps.Contains(name)) {
					StartRun(window, Trigger.Hotkey);
					return (IntPtr)1;
				}
			}
		}
		return Native.CallNextHookEx(_hook, code, wParam, lParam);
	}

	Config CurrentConfig() {
		if (_config == null || (DateTime.Now - _configTime).TotalSeconds > 3) {
			_config = Config.Load();
			_configTime = DateTime.Now;
		}
		return _config;
	}

	public enum Trigger { Hotkey, Button, Enter }
	volatile bool _enterRun;
	readonly ManualResetEvent _sendAsIs = new ManualResetEvent(false);

	void StartRun(IntPtr window, Trigger trigger) {
		if (window == IntPtr.Zero || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) {
			return;
		}
		SetBusy(true);
		var thread = new Thread(() => {
			if (trigger == Trigger.Enter) RunEnter(window);
			else Run(window, trigger == Trigger.Hotkey);
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();
	}

	void SetBusy(bool busy) {
		try {
			_button.BeginInvoke(new Action(() => _button.Busy = busy));
		} catch {
		}
	}

	// Keeps the button on the active AyuGram window, hides it otherwise.
	void Follow() {
		var config = CurrentConfig();
		var foreground = Native.GetForegroundWindow();
		if (foreground == _button.Handle || _button.Dragging) {
			return;
		}
		var window = foreground != IntPtr.Zero ? Native.GetAncestor(foreground, 2) : IntPtr.Zero; // GA_ROOT
		var show = config.Enabled
			&& config.Button
			&& window != IntPtr.Zero
			&& config.Apps.Contains(ProcessName(window))
			&& Native.IsWindowVisible(window)
			&& !Native.IsIconic(window);
		if (!show) {
			if (_button.Visible && _busy == 0) _button.Hide();
			return;
		}
		Native.RECT rect;
		if (!Native.GetClientRect(window, out rect) || rect.Right < 300 || rect.Bottom < 200) {
			if (_button.Visible) _button.Hide();
			return;
		}
		var origin = new Point(0, 0);
		Native.ClientToScreen(window, ref origin);
		var client = new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
		var scale = 1f;
		try {
			var dpi = Native.GetDpiForWindow(window);
			if (dpi > 0) scale = dpi / 96f;
		} catch {
		}
		_target = window;
		_button.Client = client;
		_button.Place(client, scale, config.ButtonRight, config.ButtonBottom);
		if (!_button.Visible) _button.Show();
	}

	void SaveSetting(string name, object value) {
		try {
			var path = Path.Combine(Config.Folder(), "ayu_fancy.json");
			var serializer = new JavaScriptSerializer();
			var json = File.Exists(path)
				? serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF')) as Dictionary<string, object>
				: null;
			if (json == null) json = new Dictionary<string, object>();
			json[name] = value;
			File.WriteAllText(path, serializer.Serialize(json), new UTF8Encoding(false));
			_config = null;
		} catch (Exception e) {
			Log.Write("setting not saved: " + e.Message);
		}
	}

	void SaveButtonPlace(int right, int bottom) {
		try {
			var path = Path.Combine(Config.Folder(), "ayu_fancy.json");
			var serializer = new JavaScriptSerializer();
			var json = File.Exists(path)
				? serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF')) as Dictionary<string, object>
				: null;
			if (json == null) json = new Dictionary<string, object>();
			json["button_right"] = right;
			json["button_bottom"] = bottom;
			File.WriteAllText(path, serializer.Serialize(json), new UTF8Encoding(false));
			_config = null;
			Log.Write("button moved to " + right + "," + bottom);
		} catch (Exception e) {
			Log.Write("button place not saved: " + e.Message);
		}
	}

	void Show(string text, IntPtr window, int milliseconds) {
		try {
			_osd.BeginInvoke(new Action(() => _osd.ShowText(text, window, milliseconds)));
		} catch {
		}
	}

	static T Retry<T>(Func<T> action, T fallback) {
		for (var i = 0; i < 10; ++i) {
			try {
				return action();
			} catch (ExternalException) {
				Thread.Sleep(40);
			}
		}
		return fallback;
	}

	static void RetryDo(Action action) {
		Retry<bool>(() => { action(); return true; }, false);
	}

	static byte[] ReadBytes(IDataObject data, string format) {
		if (data == null || !data.GetDataPresent(format)) return null;
		var stream = data.GetData(format) as Stream;
		if (stream == null) return null;
		var copy = new MemoryStream();
		stream.CopyTo(copy);
		var bytes = copy.ToArray();
		var size = bytes.Length;
		while (size > 0 && bytes[size - 1] == 0) --size;
		return bytes.Take(size).ToArray();
	}

	// Field text from the clipboard: Telegram's own formats, else plain text.
	static Tagged ReadClipboard() {
		var data = Retry<IDataObject>(() => Clipboard.GetDataObject(), null);
		if (data == null) return null;
		var textBytes = ReadBytes(data, Core.TextMime);
		if (textBytes != null && textBytes.Length > 0) {
			var text = new UTF8Encoding(false).GetString(textBytes);
			var tags = Core.DeserializeTags(ReadBytes(data, Core.TagsMime), text.Length);
			return new Tagged(text, tags);
		}
		if (data.GetDataPresent(DataFormats.UnicodeText)) {
			var plain = data.GetData(DataFormats.UnicodeText) as string;
			if (!string.IsNullOrEmpty(plain)) return new Tagged(plain.Replace("\r\n", "\n"), null);
		}
		return null;
	}

	static void WriteClipboard(Tagged text) {
		var data = new DataObject();
		data.SetData(DataFormats.UnicodeText, text.Text.Replace("\n", "\r\n"));
		data.SetData(Core.TextMime, new MemoryStream(new UTF8Encoding(false).GetBytes(text.Text)));
		if (text.Tags.Count > 0) {
			data.SetData(Core.TagsMime, new MemoryStream(Core.SerializeTags(text.Tags)));
		}
		data.SetData(DataFormats.Html, new MemoryStream(Core.CfHtml(Core.ToHtml(text))));
		RetryDo(() => Clipboard.SetDataObject(data, true));
	}

	static Tagged CopyFromField(bool selectAll) {
		RetryDo(() => Clipboard.Clear());
		if (selectAll) {
			Native.Press(Native.VK_CONTROL, 'A');
			Thread.Sleep(60);
		}
		Native.Press(Native.VK_CONTROL, 'C');
		for (var i = 0; i < (selectAll ? 30 : 12); ++i) {
			Thread.Sleep(40);
			var text = ReadClipboard();
			if (text != null) return text;
		}
		return null;
	}

	static bool Same(Tagged a, Tagged b) {
		return a != null && b != null && a.Text == b.Text;
	}

	void Run(IntPtr window, bool fromHotkey) {
		string saved = null;
		var restore = true;
		try {
			// Ctrl+Shift still held would turn Ctrl+C into Ctrl+Shift+C.
			for (var i = 0; fromHotkey && i < 60 && (Native.Down(Native.VK_SHIFT) || Native.Down(Native.VK_F)); ++i) {
				Thread.Sleep(25);
			}
			if (Native.Down(Native.VK_SHIFT)) keybd_up(Native.VK_SHIFT);
			if (Native.GetForegroundWindow() != window) {
				// The button never activates itself, but be sure the field gets the keys.
				Native.SetForegroundWindow(window);
				Thread.Sleep(80);
			}
			saved = Retry<string>(() => Clipboard.ContainsText() ? Clipboard.GetText() : null, null);

			var selectAll = false;
			var source = CopyFromField(false);
			if (source == null || source.Text.Trim().Length == 0) {
				selectAll = true;
				source = CopyFromField(true);
			}
			if (source == null || source.Text.Trim().Length == 0) {
				Show("\u2728 Сначала напиши текст в поле сообщения", window, 2500);
				return;
			}
			var config = Config.Load();
			Show("\u2728 Оформляю\u2026", window, 0);
			var result = Engines.Format(config, source);
			if (result.Error.Length > 0) {
				Show("\u2728 " + result.Error, window, 5000);
				return;
			}
			if (Native.GetForegroundWindow() != window) {
				WriteClipboard(result.Text);
				restore = false;
				Show("\u2728 Готово, результат в буфере: вставь Ctrl+V", window, 5000);
				return;
			}
			// The user may have kept typing while the model was thinking.
			var now = CopyFromField(selectAll);
			if (!Same(now, source)) {
				WriteClipboard(result.Text);
				restore = false;
				Show("\u2728 Текст поменялся, пока ждал ответ. Результат в буфере: Ctrl+V", window, 6000);
				return;
			}
			WriteClipboard(result.Text);
			if (selectAll) {
				Native.Press(Native.VK_CONTROL, 'A');
				Thread.Sleep(50);
			}
			Native.Press(Native.VK_CONTROL, 'V');
			// The old clipboard is not put back: a slow client would paste it.
			restore = false;
			Thread.Sleep(300);
			Show(result.Lost > 0
				? string.Format("\u2728 Готово ({0}, {1:0.0} с), {2} эмодзи не вернулись. Ctrl+Z вернёт как было", result.Engine, result.Milliseconds / 1000.0, result.Lost)
				: string.Format("\u2728 Готово ({0}, {1:0.0} с). Ctrl+Z вернёт как было", result.Engine, result.Milliseconds / 1000.0), window, 3500);
		} catch (Exception e) {
			Log.Write("run failed: " + e);
			Show("\u2728 Ошибка: " + e.Message, window, 5000);
		} finally {
			if (restore) {
				var text = saved;
				RetryDo(() => {
					if (text != null) Clipboard.SetText(text);
					else Clipboard.Clear();
				});
			}
			Interlocked.Exchange(ref _busy, 0);
			SetBusy(false);
		}
	}

	static bool HasWords(string text, int count) {
		return Regex.Matches(text, "[\\p{L}\\p{N}]+").Count >= count;
	}

	// Enter in AyuGram: format the whole message, then send it. Esc sends it
	// as typed; any failure or timeout sends it as typed too.
	void RunEnter(IntPtr window) {
		string saved = null;
		_sendAsIs.Reset();
		_enterRun = true;
		try {
			saved = Retry<string>(() => Clipboard.ContainsText() ? Clipboard.GetText() : null, null);
			var source = CopyFromField(true);
			// Search boxes, single words, stickers, empty field: Enter as usual.
			if (source == null || !HasWords(source.Text, 2)) {
				Native.Tap(Native.VK_RETURN);
				return;
			}
			var config = CurrentConfig();
			Show("\u2728 Оформляю и отправляю\u2026  Esc: отправить как есть", window, 0);
			Engines.Result result = null;
			var worker = new Thread(() => {
				try { result = Engines.Format(config, source); } catch (Exception e) { result = new Engines.Result { Error = e.Message }; }
			});
			worker.IsBackground = true;
			worker.Start();
			var deadline = DateTime.Now.AddSeconds(config.EnterTimeout);
			while (worker.IsAlive && DateTime.Now < deadline && !_sendAsIs.WaitOne(30)) {
			}
			var asIs = _sendAsIs.WaitOne(0);
			if (Native.GetForegroundWindow() != window) {
				Show("\u2728 Окно сменилось, не отправил", window, 4000);
				return;
			}
			var now = CopyFromField(true);
			if (!Same(now, source)) {
				Show("\u2728 Текст поменялся, пока оформлял. Жми Enter ещё раз", window, 5000);
				return;
			}
			if (asIs || worker.IsAlive || result == null || result.Error.Length > 0 || result.Text == null) {
				var reason = asIs ? "Esc" : worker.IsAlive ? ("дольше " + config.EnterTimeout + " с") : (result == null ? "нет ответа" : result.Error);
				Native.Tap(Native.VK_RETURN);
				Show("\u2728 Отправил как есть: " + reason, window, 4000);
				return;
			}
			WriteClipboard(result.Text);
			Native.Press(Native.VK_CONTROL, 'A');
			Thread.Sleep(40);
			Native.Press(Native.VK_CONTROL, 'V');
			Thread.Sleep(150);
			Native.Tap(Native.VK_RETURN);
			Show(string.Format("\u2728 Оформил и отправил ({0}, {1:0.0} с)", result.Engine, result.Milliseconds / 1000.0), window, 2500);
			// The message is sent, the user's clipboard can come back now.
			Thread.Sleep(1500);
		} catch (Exception e) {
			Log.Write("enter failed: " + e);
			Show("\u2728 Ошибка, не отправил: " + e.Message, window, 5000);
		} finally {
			var text = saved;
			RetryDo(() => {
				if (text != null) Clipboard.SetText(text);
			});
			_enterRun = false;
			Interlocked.Exchange(ref _busy, 0);
			SetBusy(false);
		}
	}

	static void keybd_up(int key) {
		Native.keybd_event((byte)key, 0, 2, UIntPtr.Zero);
	}
}

public static class Program {
	// --selftest <in.txt> <out.txt>: formats the text the way Ctrl+Shift+F
	// does and writes a report, for AYU_FANCY.cmd.
	static int SelfTest(string input, string output) {
		var text = File.ReadAllText(input, Encoding.UTF8).TrimStart('\uFEFF').Replace("\r\n", "\n").Trim();
		var result = Engines.Format(Config.Load(), new Tagged(text, null));
		var report = new StringBuilder();
		report.AppendLine("engine: " + result.Engine);
		report.AppendLine("seconds: " + (result.Milliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
		report.AppendLine("error: " + result.Error);
		if (result.Text != null) {
			report.AppendLine("text: " + result.Text.Text.Replace("\n", "\\n"));
			report.AppendLine("tags: " + string.Join(" ", result.Text.Tags.Select(t => t.Offset + "+" + t.Length + ":" + t.Id.Replace('\\', '|'))));
		}
		File.WriteAllText(output, report.ToString(), new UTF8Encoding(false));
		return result.Error.Length == 0 && result.Text != null && result.Text.Text.Length > 0 ? 0 : 1;
	}

	[STAThread]
	public static int Main(string[] args) {
		if (args.Length == 3 && args[0] == "--selftest") {
			return SelfTest(args[1], args[2]);
		}
		if (args.Length == 1 && args[0] == "--version") {
			return 0;
		}
		bool created;
		using (var mutex = new Mutex(true, "Local\\AyuFancy", out created)) {
			if (!created) {
				return 0;
			}
			try {
				Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); // per monitor v2
			} catch {
			}
			Application.EnableVisualStyles();
			Application.Run(new TrayApp());
		}
		return 0;
	}
}

} // namespace AyuFancy
