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
using System.Windows.Automation;
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

	// The same rules as system instructions for the warm Codex server: the
	// message itself is then the whole user input, fewer tokens per turn.
	public static string CleanInstructions(string extra) {
		var prompt = BuildCleanPrompt("\u0000", extra);
		var cut = prompt.IndexOf("Now the real message.", StringComparison.Ordinal);
		return prompt.Substring(0, cut).TrimEnd() + "\nThe user message is the author's Telegram message between <<< and >>>. Answer with the edited message only.";
	}

	public static string CleanInput(string html) {
		return "Message:\n<<<\n" + html + "\n>>>\nAnswer:";
	}

	// Clean mode: a careful copy editor, not a designer.
	public static string BuildCleanPrompt(string html, string extra) {
		var result = new StringBuilder();
		result.Append("You are a sharp, careful copy editor for Telegram messages. Return the same message, cleaned up so it reads like a literate native speaker typed it carefully:\n");
		result.Append("- Correct capitalization. Commas: the bare minimum only, where leaving one out is a real mistake (before что, который, если, когда, потому что and the like, between two full clauses, after a name the author addresses). No commas around пж, короче, ну, вообще, кстати, типа and other filler words. When unsure, no comma.\n");
		result.Append("- Never add ? ! : ; that the author did not type, even when the message sounds like a question. Never end the message, a paragraph or a line with a period. Fix obvious typos and agreement errors.\n");
		result.Append("- Proper nouns and brand names get capitals (Слава, ПК, Telegram); casual slang words stay as they are (телега, пж, норм, ок).\n");
		result.Append("- Readable whitespace: split a long message into short paragraphs by meaning with an empty line (<br><br>) between them; a short message stays one paragraph.\n");
		result.Append("- When the author enumerates (во-первых / во-вторых, first / second, 1 2 3), put each point on its own line with a single <br>.\n");
		result.Append("- <i>Italics</i> sparingly, only where emphasis, a term, a title or an aside really fits.\n");
		result.Append("- Only a long message with clearly different parts gets headings: <h1>, <h2>, <h3> on their own lines.\n");
		result.Append("- No bold, no emoji, no new lists, no underline, no spoilers.\n");
		result.Append("- Keep every word, the order, the author's voice, slang and profanity. Do not rephrase, shorten, add or translate anything.\n");
		result.Append("- Keep links as <a href=\"...\">text</a>, commands and codes as <code>...</code>. Tokens like ⟦1⟧ are custom emoji or mentions: copy each unchanged.\n");
		result.Append("- Output Telegram HTML only (<i> <h1> <h2> <h3> <code> <a> <br>), the message only, no explanations, no ``` fences. Do not run any commands or tools.\n");
		if (!string.IsNullOrWhiteSpace(extra)) {
			result.Append("- Author's own wishes: " + extra.Trim() + "\n");
		}
		result.Append("\nExample 1\nMessage:\n<<<\nкороче я вчера доделал бота он теперь сам режет видео в кружки осталось звук починить но это мелочи\n>>>\nAnswer:\nКороче я вчера доделал бота, он теперь сам режет видео в кружки<br><br>Осталось звук починить, <i>но это мелочи</i>\n\n");
		result.Append("Example 2\nMessage:\n<<<\nслав глянь пж логи там опять ошибка во первых бот не отвечает во вторых телега тупит. ты когда будешь\n>>>\nAnswer:\nСлав, глянь пж логи, там опять ошибка<br><br>Во-первых бот не отвечает<br>Во-вторых телега тупит<br><br>Ты когда будешь\n\n");
		result.Append("Now the real message.\nMessage:\n<<<\n" + html + "\n>>>\nAnswer:\n");
		return result.ToString();
	}

	static bool IsEmojiCode(int code) {
		return (code >= 0x1F300 && code <= 0x1FAFF) || (code >= 0x2600 && code <= 0x27BF)
			|| (code >= 0x2B00 && code <= 0x2BFF) || code == 0xFE0F || code == 0x200D
			|| (code >= 0x1F1E6 && code <= 0x1F1FF);
	}

	// Arthur 08.10: no ? ! : ; the author did not type (a mark counts as typed
	// when the original has it right after the same word), times like 19:00
	// stay, and no period at the end of a line or of the message.
	static bool ExtraMark(string original, string text, int i, StringBuilder output) {
		var c = text[i];
		if (c == '.') {
			if (i > 0 && text[i - 1] == '.') return false;
			var k = i + 1;
			if (k < text.Length && text[k] == '.') return false;
			while (k < text.Length && (text[k] == ' ' || text[k] == '\t')) ++k;
			return k >= text.Length || text[k] == '\n';
		}
		if (c != '?' && c != '!' && c != ':' && c != ';') return false;
		if ((c == ':' || c == ';') && output.Length > 0 && char.IsDigit(output[output.Length - 1])
			&& i + 1 < text.Length && char.IsDigit(text[i + 1])) return false;
		// the word right before the mark, skipping spaces and repeated marks
		var end = output.Length;
		while (end > 0 && (output[end - 1] == ' ' || output[end - 1] == c)) --end;
		var start = end;
		while (start > 0 && char.IsLetterOrDigit(output[start - 1])) --start;
		var word = output.ToString(start, end - start);
		if (word.Length == 0) return original.IndexOf(c) < 0;
		var mark = Regex.Escape(c.ToString());
		return !Regex.IsMatch(original, "(?<![\\p{L}\\p{N}])" + Regex.Escape(word) + "[ " + mark + "]*" + mark, RegexOptions.IgnoreCase);
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
			if (!drop && !pair) drop = ExtraMark(original.Text, text, i, output);
			if (drop && !IsEmojiCode(code)) {
				// "будешь ?" → "будешь": the space before a dropped mark goes too
				var next = i + 1 < text.Length ? text[i + 1] : '\n';
				if (output.Length > 0 && output[output.Length - 1] == ' ' && char.IsWhiteSpace(next)) output.Length -= 1;
			} else if (drop) {
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
	// Enter waits at most this long, then the message goes as typed.
	public int EnterBudgetMs = 5000;
	// Codex waits this long before Groq's answer is taken. 0: the first good
	// answer wins; `codex exec` takes 4-13 s, Groq about 1.5 s.
	public int CodexPreferMs = 0;
	// Groq gpt-oss reasoning: "medium" edits noticeably better than "low"
	// and still answers in about 1.5 s; "high" takes 3-6 s.
	public string GroqEffort = "medium";
	// Groq models tried in turn when the main one hits its free limit.
	public string[] GroqSpare = { "qwen/qwen3.8-27b", "openai/gpt-oss-20b" };
	// Formats in the background while the user pauses typing.
	public bool Prefetch = true;
	// Keep one warm `codex app-server` instead of `codex exec` per message.
	public bool CodexServer = true;
	// Dictation: Groq Whisper, the most accurate free one by default.
	public string WhisperModel = "whisper-large-v3";
	public string WhisperLanguage = "";
	public bool Dictation = true;
	// Live dictation: every phrase is turned into this language at once
	// (Arthur 08.10: speak in any language, Russian with commas appears).
	// "" = text as spoken.
	public string DictationLanguage = "ru";
	public bool Button = true;
	// The "Оформление ВКЛ/ВЫКЛ" pill next to the ✨ button.
	public bool Toggle = true;
	// Button position from the bottom right corner of the AyuGram window,
	// in pixels at 100% scale: just above the send button by default.
	public int ButtonRight = 14;
	public int ButtonBottom = 62;
	// Pill position from the top left of the AyuGram window, 100% scale;
	// -1 = top centre (ayu-fancy-drag-v1: drag it anywhere with the mouse).
	public int PillX = -1;
	// ayu-fancy-compact-v1: only ✨ is seen, the mouse over it opens all four
	public bool Compact = true;
	public int PillY = -1;
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
					result.Compact = Bool(json, "compact", true);
					result.Engine = Str(json, "engine", result.Engine).ToLowerInvariant();
					result.CodexPath = Str(json, "codex_path", result.CodexPath);
					result.CodexModel = Str(json, "codex_model", result.CodexModel);
					result.CodexEffort = Str(json, "codex_effort", result.CodexEffort);
					result.CodexUserConfig = Bool(json, "codex_user_config", false);
					result.Key = Str(json, "key", result.Key);
					result.BaseUrl = Str(json, "base_url", result.BaseUrl).TrimEnd('/');
					result.Model = Str(json, "model", result.Model);
					result.GroqEffort = Str(json, "groq_effort", result.GroqEffort).ToLowerInvariant();
					var spare = Str(json, "groq_spare", null);
					if (spare != null) result.GroqSpare = spare.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
					result.Style = Str(json, "style", result.Style);
					result.Button = Bool(json, "button", true);
					result.Toggle = Bool(json, "toggle", true);
					result.Mode = Str(json, "mode", result.Mode).ToLowerInvariant();
					result.OnEnter = Bool(json, "on_enter", true);
					result.Prefetch = Bool(json, "prefetch", true);
					result.CodexServer = Bool(json, "codex_server", true);
					result.WhisperModel = Str(json, "whisper_model", result.WhisperModel);
					result.WhisperLanguage = Str(json, "whisper_language", result.WhisperLanguage);
					result.Dictation = Bool(json, "dictation", true);
					result.DictationLanguage = Str(json, "dictation_language", result.DictationLanguage);
					object budget;
					if (json.TryGetValue("enter_budget_ms", out budget) && budget is int) result.EnterBudgetMs = Math.Max(1000, (int)budget);
					object prefer;
					if (json.TryGetValue("codex_prefer_ms", out prefer) && prefer is int) result.CodexPreferMs = Math.Max(0, (int)prefer);
					object timeout;
					if (json.TryGetValue("enter_timeout", out timeout) && timeout is int) result.EnterTimeout = Math.Max(3, (int)timeout);
					object number;
					if (json.TryGetValue("button_right", out number) && number is int) result.ButtonRight = (int)number;
					if (json.TryGetValue("button_bottom", out number) && number is int) result.ButtonBottom = (int)number;
					if (json.TryGetValue("pill_x", out number) && number is int) result.PillX = (int)number;
					if (json.TryGetValue("pill_y", out number) && number is int) result.PillY = (int)number;
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

// Microphone through MCI, built into Windows: 16 kHz mono WAV, what
// Whisper wants anyway, no drivers and nothing to install.
public static class Recorder {
	[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
	static extern int mciSendString(string command, StringBuilder answer, int answerLength, IntPtr callback);
	[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
	static extern bool mciGetErrorString(int error, StringBuilder text, int length);

	static void Command(string command) {
		var error = mciSendString(command, null, 0, IntPtr.Zero);
		if (error != 0) {
			var text = new StringBuilder(256);
			mciGetErrorString(error, text, 256);
			throw new Exception(text.ToString());
		}
	}

	public static void Start() {
		try { Command("close ayurec"); } catch { }
		Command("open new type waveaudio alias ayurec");
		Command("set ayurec time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");
		Command("record ayurec");
	}

	public static string Stop() {
		var path = Path.Combine(Path.GetTempPath(), "ayufancy-" + Guid.NewGuid().ToString("N") + ".wav");
		Command("stop ayurec");
		Command("save ayurec \"" + path + "\"");
		Command("close ayurec");
		return path;
	}

	public static void Cancel() {
		try { Command("stop ayurec"); } catch { }
		try { Command("close ayurec"); } catch { }
	}

	// 16 kHz mono 16-bit PCM as a WAV file.
	public static byte[] Wav(byte[] pcm, int offset, int count) {
		var stream = new MemoryStream();
		var writer = new BinaryWriter(stream);
		writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
		writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
		writer.Write((short)2); writer.Write((short)16);
		writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count);
		writer.Write(pcm, offset, count);
		writer.Flush();
		return stream.ToArray();
	}

	// One second of silence, for the key check in AYU_FANCY.cmd.
	public static byte[] SilenceWav() {
		var stream = new MemoryStream();
		var writer = new BinaryWriter(stream);
		var data = 32000;
		writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
		writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
		writer.Write((short)2); writer.Write((short)16);
		writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(data);
		writer.Write(new byte[data]);
		writer.Flush();
		return stream.ToArray();
	}
}

// Microphone straight into memory (waveIn, 100 ms buffers, polled): live
// dictation reads the sound while recording goes on. MCI could only save
// the whole recording at the end.
public class LiveMic {
	[StructLayout(LayoutKind.Sequential)]
	struct WaveFormat { public short Tag, Channels; public int Rate, BytesPerSec; public short Align, Bits, Extra; }
	[StructLayout(LayoutKind.Sequential)]
	struct WaveHdr { public IntPtr Data; public int Length, Recorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }
	[DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr handle, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
	[DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
	[DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
	[DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
	[DllImport("winmm.dll")] static extern int waveInStart(IntPtr handle);
	[DllImport("winmm.dll")] static extern int waveInReset(IntPtr handle);
	[DllImport("winmm.dll")] static extern int waveInClose(IntPtr handle);

	const int BufferBytes = 3200; // 100 ms
	IntPtr _handle;
	readonly List<IntPtr> _headers = new List<IntPtr>();
	readonly MemoryStream _pcm = new MemoryStream();
	readonly object _lock = new object();
	volatile bool _running;
	Thread _poller;

	public void Start() {
		var format = new WaveFormat { Tag = 1, Channels = 1, Rate = 16000, BytesPerSec = 32000, Align = 2, Bits = 16 };
		var error = waveInOpen(out _handle, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0);
		if (error != 0) throw new Exception("микрофон не открылся (waveIn " + error + ")");
		var size = Marshal.SizeOf(typeof(WaveHdr));
		for (var i = 0; i < 20; ++i) {
			var header = Marshal.AllocHGlobal(size);
			Marshal.StructureToPtr(new WaveHdr { Data = Marshal.AllocHGlobal(BufferBytes), Length = BufferBytes }, header, false);
			waveInPrepareHeader(_handle, header, size);
			waveInAddBuffer(_handle, header, size);
			_headers.Add(header);
		}
		_running = true;
		waveInStart(_handle);
		_poller = new Thread(Poll) { IsBackground = true };
		_poller.Start();
	}

	void Poll() {
		var size = Marshal.SizeOf(typeof(WaveHdr));
		while (_running) {
			Collect(size, true);
			Thread.Sleep(30);
		}
	}

	void Collect(int size, bool requeue) {
		foreach (var header in _headers) {
			var h = (WaveHdr)Marshal.PtrToStructure(header, typeof(WaveHdr));
			if ((h.Flags & 1) == 0) continue; // WHDR_DONE
			if (h.Recorded > 0) {
				var bytes = new byte[h.Recorded];
				Marshal.Copy(h.Data, bytes, 0, h.Recorded);
				lock (_lock) _pcm.Write(bytes, 0, bytes.Length);
			}
			waveInUnprepareHeader(_handle, header, size);
			if (!requeue) continue;
			Marshal.StructureToPtr(new WaveHdr { Data = h.Data, Length = BufferBytes }, header, false);
			waveInPrepareHeader(_handle, header, size);
			waveInAddBuffer(_handle, header, size);
		}
	}

	public byte[] Snapshot() {
		lock (_lock) return _pcm.ToArray();
	}

	public void Stop() {
		if (!_running) return;
		_running = false;
		if (_poller != null) _poller.Join(500);
		waveInReset(_handle); // hands back the buffers, partly filled ones too
		Collect(Marshal.SizeOf(typeof(WaveHdr)), false);
		waveInClose(_handle);
		foreach (var header in _headers) {
			var h = (WaveHdr)Marshal.PtrToStructure(header, typeof(WaveHdr));
			Marshal.FreeHGlobal(h.Data);
			Marshal.FreeHGlobal(header);
		}
		_headers.Clear();
	}
}

public static class Whisper {
	public static Answer Transcribe(Config config, byte[] wav) {
		int status;
		return Transcribe(config, wav, config.WhisperModel, null, out status);
	}

	// Live dictation: one phrase; the text dictated so far as context keeps
	// names and style the same across phrases.
	public static Answer Transcribe(Config config, byte[] wav, string model, string context, out int status) {
		status = 0;
		var answer = new Answer { Engine = "Whisper" };
		if (string.IsNullOrEmpty(config.Key)) {
			answer.Error = "для диктовки нужен бесплатный ключ Groq, запусти AYU_FANCY.cmd";
			return answer;
		}
		try {
			Engines.TuneNet(); // no Expect: 100-continue, one round trip less per phrase
			var boundary = "----ayufancy" + Guid.NewGuid().ToString("N");
			var request = (HttpWebRequest)WebRequest.Create(config.BaseUrl + "/audio/transcriptions");
			request.Method = "POST";
			request.ContentType = "multipart/form-data; boundary=" + boundary;
			request.Headers["Authorization"] = "Bearer " + config.Key;
			request.Timeout = 60000;
			request.ReadWriteTimeout = 60000;
			var body = new MemoryStream();
			Action<string, string> field = (name, value) => {
				var bytes = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n");
				body.Write(bytes, 0, bytes.Length);
			};
			field("model", model);
			field("response_format", "json");
			field("temperature", "0");
			if (!string.IsNullOrEmpty(config.WhisperLanguage)) field("language", config.WhisperLanguage);
			var hint = "Привет! Это сообщение в Telegram, с заглавными буквами и пунктуацией. Hi, English words stay in English.";
			if (!string.IsNullOrEmpty(context)) hint = context.Length > 300 ? context.Substring(context.Length - 300) : context;
			field("prompt", hint);
			var head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
			body.Write(head, 0, head.Length);
			body.Write(wav, 0, wav.Length);
			var tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
			body.Write(tail, 0, tail.Length);
			var payload = body.ToArray();
			request.ContentLength = payload.Length;
			using (var stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
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
				using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
			}
			var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
			if (status == 200 && json != null && json.ContainsKey("text")) {
				answer.Text = ((json["text"] as string) ?? "").Trim();
				return answer;
			}
			Log.Write("whisper " + status + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
		} catch (Exception e) {
			Log.Write("whisper failed: " + e.Message);
		}
		answer.Error = status == 401 ? "Groq: ключ не подходит" : status == 429 ? "Groq: лимит, подожди минуту" : status != 0 ? ("Whisper: ошибка " + status) : "Whisper: нет сети";
		return answer;
	}
}

// One warm `codex app-server` for all messages: no process start, no
// login and connection setup per message, and our short editor rules as the
// base instructions instead of the coding agent's long ones.
public class CodexServer {
	static readonly object Gate = new object();
	static CodexServer _current;

	readonly string _signature;
	readonly Config _config;
	readonly string _instructions;
	readonly string _workDir;
	Process _process;
	readonly object _write = new object();
	int _nextId = 100;
	readonly Dictionary<int, Action<Dictionary<string, object>>> _pending = new Dictionary<int, Action<Dictionary<string, object>>>();
	readonly Dictionary<string, Turn> _turns = new Dictionary<string, Turn>();
	readonly Queue<string> _spare = new Queue<string>();
	bool _spareStarting;
	public bool Alive;

	class Turn {
		public readonly StringBuilder Delta = new StringBuilder();
		public string Final;
		public string Error;
		public readonly ManualResetEvent Done = new ManualResetEvent(false);
	}

	CodexServer(Config config, string codex, string signature) {
		_config = config;
		_signature = signature;
		_instructions = Core.CleanInstructions(config.Style);
		_workDir = Path.Combine(Path.GetTempPath(), "ayufancy-server");
		Directory.CreateDirectory(_workDir);
		var args = new List<string> { "app-server" };
		foreach (var feature in Engines.HeavyFeatures) {
			args.Add("--disable");
			args.Add(feature);
		}
		var info = new ProcessStartInfo {
			FileName = codex,
			Arguments = string.Join(" ", args.Select(a => Engines.Quote(a))),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WorkingDirectory = _workDir,
		};
		_process = new Process { StartInfo = info, EnableRaisingEvents = true };
		_process.ErrorDataReceived += (s, e) => { };
		_process.Exited += (s, e) => { Alive = false; FailAll("Codex-сервер закрылся"); };
		_process.Start();
		_process.BeginErrorReadLine();
		var reader = new Thread(ReadLoop);
		reader.IsBackground = true;
		reader.Start();
		Alive = true;
		var init = Request("initialize", new Dictionary<string, object> {
			{ "clientInfo", new Dictionary<string, object> { { "name", "ayufancy" }, { "title", "AyuFancy" }, { "version", "1.0" } } },
		}, 30000);
		if (init == null || init.ContainsKey("error")) {
			Alive = false;
			throw new Exception("Codex-сервер не ответил на initialize");
		}
		Notify("initialized", null);
		Log.Write("codex server up");
	}

	public static string SignatureOf(Config config, string codex) {
		return codex + "|" + config.CodexModel + "|" + config.CodexEffort + "|" + config.Style;
	}

	// After a failure the server rests for a while and `codex exec` answers:
	// before, a broken server was relaunched every 10 s all day (1384 times
	// on 06.10), and each Ask waited 20 s for thread/start first.
	static readonly object PauseGate = new object();
	static DateTime _pausedUntil = DateTime.MinValue;
	const int PauseMinutes = 30;

	public static bool Paused {
		get { lock (PauseGate) return DateTime.Now < _pausedUntil; }
	}

	public static void Pause(string why) {
		lock (PauseGate) {
			if (DateTime.Now < _pausedUntil) return;
			_pausedUntil = DateTime.Now.AddMinutes(PauseMinutes);
		}
		Log.Write("codex server paused for " + PauseMinutes + " min: " + why);
		lock (Gate) {
			if (_current != null) _current.Stop();
			_current = null;
		}
	}

	// Starts (or reuses) the server and keeps a spare thread ready.
	public static CodexServer Get(Config config, string codex) {
		if (Paused) throw new Exception("на паузе после сбоя");
		var signature = SignatureOf(config, codex);
		lock (Gate) {
			if (_current != null && _current.Alive && _current._signature == signature) {
				return _current;
			}
			if (_current != null) _current.Stop();
			_current = null;
			CodexServer server;
			try {
				server = new CodexServer(config, codex, signature);
			} catch (Exception e) {
				ThreadPool.QueueUserWorkItem(_ => Pause(e.Message));
				throw;
			}
			_current = server;
			server.PrepareSpare();
			return server;
		}
	}

	public static void Warm(Config config, string codex) {
		if (Paused) return;
		try {
			Get(config, codex);
		} catch (Exception e) {
			Log.Write("codex server warm: " + e.Message);
		}
	}

	void Stop() {
		Alive = false;
		try { _process.Kill(); } catch { }
	}

	void Send(Dictionary<string, object> message) {
		var line = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(message);
		lock (_write) {
			_process.StandardInput.WriteLine(line);
			_process.StandardInput.Flush();
		}
	}

	void Notify(string method, Dictionary<string, object> parameters) {
		var message = new Dictionary<string, object> { { "method", method } };
		if (parameters != null) message["params"] = parameters;
		Send(message);
	}

	Dictionary<string, object> Request(string method, Dictionary<string, object> parameters, int timeoutMs) {
		var done = new ManualResetEvent(false);
		Dictionary<string, object> answer = null;
		int id;
		lock (_pending) {
			id = _nextId++;
			_pending[id] = (message) => { answer = message; done.Set(); };
		}
		Send(new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", parameters } });
		if (!done.WaitOne(timeoutMs)) {
			lock (_pending) _pending.Remove(id);
			return null;
		}
		return answer;
	}

	static Dictionary<string, object> Dict(object value) {
		return value as Dictionary<string, object>;
	}

	static string Str(Dictionary<string, object> json, string name) {
		object value;
		return json != null && json.TryGetValue(name, out value) ? value as string : null;
	}

	void ReadLoop() {
		var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
		try {
			string line;
			while ((line = _process.StandardOutput.ReadLine()) != null) {
				Dictionary<string, object> message;
				try {
					message = Dict(serializer.DeserializeObject(line));
				} catch {
					continue;
				}
				if (message == null) continue;
				object idValue;
				var hasId = message.TryGetValue("id", out idValue) && idValue is int;
				var method = Str(message, "method");
				if (hasId && method == null) {
					Action<Dictionary<string, object>> callback = null;
					lock (_pending) {
						if (_pending.TryGetValue((int)idValue, out callback)) _pending.Remove((int)idValue);
					}
					if (callback != null) callback(message);
					continue;
				}
				if (hasId && method != null) {
					// A server request (approval and the like): never wanted here.
					Send(new Dictionary<string, object> { { "id", idValue }, { "error", new Dictionary<string, object> { { "code", -32601 }, { "message", "not supported" } } } });
					continue;
				}
				OnNotification(method, Dict(message.ContainsKey("params") ? message["params"] : null));
			}
		} catch {
		}
		Alive = false;
		FailAll("Codex-сервер закрылся");
	}

	Turn TurnOf(Dictionary<string, object> parameters) {
		var thread = Str(parameters, "threadId");
		if (thread == null) return null;
		lock (_turns) {
			Turn turn;
			return _turns.TryGetValue(thread, out turn) ? turn : null;
		}
	}

	void OnNotification(string method, Dictionary<string, object> parameters) {
		if (method == null || parameters == null) return;
		var turn = TurnOf(parameters);
		if (turn == null) return;
		if (method == "item/agentMessage/delta") {
			lock (turn) turn.Delta.Append(Str(parameters, "delta") ?? "");
		} else if (method == "item/completed") {
			var item = Dict(parameters.ContainsKey("item") ? parameters["item"] : null);
			if (Str(item, "type") == "agentMessage") turn.Final = Str(item, "text");
		} else if (method == "error") {
			object retry;
			var willRetry = parameters.TryGetValue("willRetry", out retry) && retry is bool && (bool)retry;
			var message = Str(Dict(parameters.ContainsKey("error") ? parameters["error"] : null), "message") ?? "ошибка";
			if (!willRetry || message.IndexOf("401", StringComparison.Ordinal) >= 0 || message.IndexOf("nauthorized", StringComparison.Ordinal) >= 0) {
				turn.Error = message;
				turn.Done.Set();
			}
		} else if (method == "turn/completed") {
			var data = Dict(parameters.ContainsKey("turn") ? parameters["turn"] : null);
			if (Str(data, "status") == "failed") {
				turn.Error = Str(Dict(data != null && data.ContainsKey("error") ? data["error"] : null), "message") ?? "turn failed";
			}
			turn.Done.Set();
		}
	}

	void FailAll(string error) {
		lock (_turns) {
			foreach (var turn in _turns.Values) {
				if (turn.Error == null) turn.Error = error;
				turn.Done.Set();
			}
		}
		lock (_pending) {
			foreach (var callback in _pending.Values.ToList()) {
				try { callback(new Dictionary<string, object> { { "error", error } }); } catch { }
			}
			_pending.Clear();
		}
	}

	string StartThread() {
		var parameters = new Dictionary<string, object> {
			{ "ephemeral", true },
			{ "sandbox", "read-only" },
			{ "approvalPolicy", "never" },
			{ "cwd", _workDir },
			{ "baseInstructions", _instructions },
		};
		if (!string.IsNullOrEmpty(_config.CodexModel)) parameters["model"] = _config.CodexModel;
		var answer = Request("thread/start", parameters, 20000);
		var result = Dict(answer != null && answer.ContainsKey("result") ? answer["result"] : null);
		var thread = Dict(result != null && result.ContainsKey("thread") ? result["thread"] : null);
		var id = Str(thread, "id");
		if (id == null) {
			var error = Dict(answer != null && answer.ContainsKey("error") ? answer["error"] : null);
			throw new Exception("thread/start: " + (Str(error, "message") ?? "нет ответа"));
		}
		return id;
	}

	void PrepareSpare() {
		lock (_spare) {
			if (_spareStarting || _spare.Count > 0) return;
			_spareStarting = true;
		}
		var worker = new Thread(() => {
			try {
				var id = StartThread();
				lock (_spare) _spare.Enqueue(id);
			} catch (Exception e) {
				Log.Write("spare thread: " + e.Message);
				Pause("spare thread: " + e.Message);
			} finally {
				lock (_spare) _spareStarting = false;
			}
		});
		worker.IsBackground = true;
		worker.Start();
	}

	public Answer Ask(string input, int timeoutMs) {
		var answer = new Answer { Engine = "Codex" };
		string thread = null;
		lock (_spare) {
			if (_spare.Count > 0) thread = _spare.Dequeue();
		}
		if (thread == null) thread = StartThread();
		PrepareSpare();
		var turn = new Turn();
		lock (_turns) _turns[thread] = turn;
		try {
			var parameters = new Dictionary<string, object> {
				{ "threadId", thread },
				{ "input", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", input } } } },
			};
			if (!string.IsNullOrEmpty(_config.CodexEffort)) parameters["effort"] = _config.CodexEffort;
			var started = Request("turn/start", parameters, 15000);
			if (started == null || started.ContainsKey("error")) {
				var error = Dict(started != null && started.ContainsKey("error") ? started["error"] : null);
				answer.Error = "Codex: " + (Str(error, "message") ?? "turn/start без ответа");
				return answer;
			}
			if (!turn.Done.WaitOne(timeoutMs)) {
				answer.Error = "Codex не ответил за " + (timeoutMs / 1000) + " с";
				return answer;
			}
			if (turn.Error != null) {
				answer.Error = (turn.Error.IndexOf("401", StringComparison.Ordinal) >= 0 || turn.Error.IndexOf("nauthorized", StringComparison.Ordinal) >= 0)
					? "Codex не вошёл в аккаунт, запусти AYU_FANCY.cmd"
					: "Codex: " + turn.Error;
				return answer;
			}
			string text;
			lock (turn) text = turn.Final ?? turn.Delta.ToString();
			answer.Text = (text ?? "").Trim();
			if (answer.Text.Length == 0) answer.Error = "Codex вернул пустой ответ";
			return answer;
		} finally {
			lock (_turns) _turns.Remove(thread);
			try { Notify("thread/unsubscribe", new Dictionary<string, object> { { "threadId", thread } }); } catch { }
		}
	}
}

public static class Engines {
	public static readonly string[] HeavyFeatures = {
		"apps", "browser_use", "browser_use_external", "computer_use",
		"image_generation", "multi_agent", "plugins", "remote_plugin",
		"shell_tool", "unified_exec", "view_image", "skill_search",
		"tool_suggest", "sleep_tool", "goals", "hooks", "in_app_browser",
		"realtime_conversation", "worktrees", "workspace_dependencies",
		"unbounded_connection_retries" };

	// Warm server first, the one-shot `codex exec` when the server can't run.
	public static Answer AskCodexFast(Config config, string codex, string html) {
		if (config.CodexServer && !CodexServer.Paused) {
			try {
				var server = CodexServer.Get(config, codex);
				var answer = server.Ask(Core.CleanInput(html), 45000);
				Log.Write("codex server: " + (answer.Error.Length > 0 ? answer.Error : "ok"));
				// A model or network error would be the same through `codex exec`.
				return answer;
			} catch (Exception e) {
				Log.Write("codex server failed: " + e.Message);
				CodexServer.Pause(e.Message);
			}
		}
		return AskCodex(config, codex, Core.BuildCleanPrompt(html, config.Style));
	}

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
		foreach (var feature in HeavyFeatures) {
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

	// Free Groq gives every model its own 200K tokens a day and 8K a minute.
	// On 07.10 at 10:00 gpt-oss-120b ran out for the day and every message
	// waited 4-7 s for Codex. Now a model that hit its limit rests until
	// Groq says, and the next one answers: 120b, then Qwen (no reasoning,
	// edits as well, ~600 tokens a message), then gpt-oss-20b.
	static readonly object CoolGate = new object();
	static readonly Dictionary<string, DateTime> CoolUntil = new Dictionary<string, DateTime>();

	public static List<string> GroqChain(Config config) {
		var chain = new List<string> { config.Model };
		foreach (var model in config.GroqSpare) {
			if (!string.IsNullOrWhiteSpace(model) && !chain.Contains(model.Trim())) chain.Add(model.Trim());
		}
		return chain;
	}

	// "Please try again in 6.99s" / "in 13m5.2s" / "in 1h2m3s": how long the model rests.
	public static TimeSpan RetryAfter(string message) {
		var match = Regex.Match(message ?? "", "try again in (?:(\\d+)h)?(?:(\\d+)m)?(?:([\\d.]+)s)?");
		var seconds = 60.0;
		if (match.Success && match.Length > "try again in ".Length) {
			seconds = 0;
			if (match.Groups[1].Success) seconds += 3600 * int.Parse(match.Groups[1].Value);
			if (match.Groups[2].Success) seconds += 60 * int.Parse(match.Groups[2].Value);
			if (match.Groups[3].Success) seconds += double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
		}
		return TimeSpan.FromSeconds(Math.Max(5, Math.Min(seconds + 1, 6 * 3600)));
	}

	public static Answer AskChat(Config config, string model, string prompt) {
		var chain = GroqChain(config);
		if (!chain.Contains(model)) chain.Insert(0, model);
		Answer last = null;
		foreach (var name in chain) {
			lock (CoolGate) {
				DateTime until;
				if (CoolUntil.TryGetValue(name, out until) && DateTime.Now < until) continue;
			}
			int status;
			string message;
			last = AskChatModel(config, name, prompt, out status, out message);
			if (last.Error.Length == 0) return last;
			if (status != 429) return last;
			var rest = RetryAfter(message);
			lock (CoolGate) CoolUntil[name] = DateTime.Now + rest;
			Log.Write("groq " + name + " rests " + (int)rest.TotalSeconds + " s, next model");
		}
		return last ?? new Answer { Engine = "Groq", Error = "Groq: все модели на лимите, подожди" };
	}

	// Keeps the HTTPS connection to Groq open while AyuGram is in front: on
	// 07.10 a round trip to Groq took 0.9 s (Google 0.05 s), a new connection
	// costs three of them (TCP, TLS, Expect: 100-continue) before the question.
	static DateTime _groqWarmAt = DateTime.MinValue;
	static int _groqWarming;

	public static void TuneNet() {
		ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
		ServicePointManager.Expect100Continue = false;
		ServicePointManager.MaxServicePointIdleTime = 300000;
	}

	public static void WarmGroq(Config config) {
		if (string.IsNullOrEmpty(config.Key) || (DateTime.Now - _groqWarmAt).TotalSeconds < 20) return;
		if (Interlocked.Exchange(ref _groqWarming, 1) == 1) return;
		_groqWarmAt = DateTime.Now;
		ThreadPool.QueueUserWorkItem(_ => {
			try {
				TuneNet();
				var request = (HttpWebRequest)WebRequest.Create(config.BaseUrl + "/models");
				request.Headers["Authorization"] = "Bearer " + config.Key;
				request.KeepAlive = true;
				request.Timeout = 15000;
				using (var response = (HttpWebResponse)request.GetResponse())
				using (var reader = new StreamReader(response.GetResponseStream())) {
					reader.ReadToEnd();
				}
			} catch {
			} finally {
				Interlocked.Exchange(ref _groqWarming, 0);
			}
		});
	}

	[ThreadStatic] public static string EffortOverride;

	static Answer AskChatModel(Config config, string model, string prompt, out int status, out string message) {
		var answer = new Answer { Engine = "Groq" };
		status = 0;
		message = "";
		try {
			TuneNet();
			var request = (HttpWebRequest)WebRequest.Create(config.BaseUrl + "/chat/completions");
			request.KeepAlive = true;
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
			if (model.Contains("gpt-oss") && !string.IsNullOrEmpty(EffortOverride)) body["reasoning_effort"] = EffortOverride;
			else if (model == config.Model && model.Contains("gpt-oss") && config.GroqEffort.Length > 0) body["reasoning_effort"] = config.GroqEffort;
			else if (model.Contains("gpt-oss")) body["reasoning_effort"] = "low";
			else if (model.Contains("qwen")) body["reasoning_effort"] = "none";
			// Free Qwen allows 1000 output tokens a minute; without a cap Groq
			// counts the default maximum and answers 429 "Request too large".
			if (model.Contains("qwen")) body["max_completion_tokens"] = 900;
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
				if (content != null) content = Regex.Replace(content, "^\\s*<think>[\\s\\S]*?</think>", "");
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
			return AskChatModel(config, "llama-3.3-70b-versatile", prompt, out status, out message);
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
		var html = Core.ToHtml(prepared);
		var prompt = Core.BuildCleanPrompt(html, config.Style);
		Tagged best = null;
		var errors = new List<string>();
		// Codex and Groq run side by side; Groq's answer is taken once Codex
		// is not there in CodexPreferMs (0 by default: first good answer wins).
		var answers = new List<Answer>();
		var codex = config.Engine != "groq" ? FindCodex(config) : null;
		var chat = config.Engine != "codex" && !string.IsNullOrEmpty(config.Key);
		Answer codexAnswer = null, chatAnswer = null;
		var codexDone = new ManualResetEvent(codex == null);
		var chatDone = new ManualResetEvent(!chat);
		if (codex != null) {
			var thread = new Thread(() => { codexAnswer = AskCodexFast(config, codex, html); codexDone.Set(); });
			thread.IsBackground = true;
			thread.Start();
		}
		if (chat) {
			var thread = new Thread(() => { chatAnswer = AskChat(config, config.Model, prompt); chatDone.Set(); });
			thread.IsBackground = true;
			thread.Start();
		}
		if (codex != null && chat) {
			if (!codexDone.WaitOne(config.CodexPreferMs) || codexAnswer.Error.Length > 0) {
				// After that: the first good answer from either engine.
				while (true) {
					var codexIn = codexDone.WaitOne(0);
					var chatIn = chatDone.WaitOne(0);
					if (codexIn && codexAnswer.Error.Length == 0) break;
					if (chatIn && chatAnswer.Error.Length == 0) break;
					if (codexIn && chatIn) break;
					WaitHandle.WaitAny(new WaitHandle[] { codexDone, chatDone }, 50);
				}
			}
		} else {
			codexDone.WaitOne();
			chatDone.WaitOne();
		}
		if (codexDone.WaitOne(0) && codexAnswer != null) answers.Add(codexAnswer);
		if (chatDone.WaitOne(0) && chatAnswer != null) answers.Add(chatAnswer);
		answers = answers.OrderBy(x => x.Error.Length > 0 ? 1 : 0).ToList(); // good first, Codex before Groq
		if (codex == null && !chat) answers.Add(new Answer { Error = "Нет ни Codex, ни ключа Groq, запусти AYU_FANCY.cmd" });
		if (answers.Count == 0) answers.Add(new Answer { Error = "нет ответа" });
		foreach (var answer in answers) {
			if (answer.Error.Length > 0) { errors.Add(answer.Error); continue; }
			int lost;
			// A heading gets an empty line under it, it can't be bold here.
			var answerHtml = Regex.Replace(Core.CleanAnswer(answer.Text), "(</h[1-6]>)(?!\\s*<br)", "$1<br>", RegexOptions.IgnoreCase);
			var restored = Core.Restore(Core.FromHtml(answerHtml, false), items, out lost);
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

	[StructLayout(LayoutKind.Sequential)]
	public struct MSG {
		public IntPtr hwnd;
		public uint message;
		public IntPtr wParam;
		public IntPtr lParam;
		public uint time;
		public int ptX;
		public int ptY;
	}

	[DllImport("user32.dll")]
	public static extern int GetMessage(out MSG msg, IntPtr window, uint min, uint max);
	[DllImport("user32.dll")]
	public static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")]
	public static extern IntPtr DispatchMessage(ref MSG msg);
	[DllImport("user32.dll")]
	public static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
	[DllImport("kernel32.dll")]
	public static extern uint GetCurrentThreadId();

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
	public delegate bool EnumProc(IntPtr window, IntPtr param);
	[DllImport("user32.dll")]
	public static extern bool EnumWindows(EnumProc proc, IntPtr param);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern int GetClassName(IntPtr window, StringBuilder name, int size);
	[DllImport("user32.dll")]
	public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
	[DllImport("user32.dll")]
	public static extern bool IsWindow(IntPtr window);
	[DllImport("user32.dll")]
	public static extern bool ShowWindow(IntPtr window, int command);
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
	public Action<int, int> Moving; // screen px the button just moved by while dragged
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
		tip.SetToolTip(this, "\u2728 Оформить красиво (Codex)  Ctrl+Shift+F\nЗажми и тащи, чтобы переставить");
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

	public string Glyph = "\u2728";
	bool _recording;

	public bool Recording {
		get { return _recording; }
		set { _recording = value; Invalidate(); }
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
		var size = (int)Math.Round(EnterToggle.ButtonSize * scale);
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
		var color = _recording ? Color.FromArgb(230, 60, 70) : _busy ? Color.FromArgb(110, 110, 130) : _hover ? Color.FromArgb(160, 105, 255) : Color.FromArgb(132, 82, 240);
		using (var brush = new SolidBrush(color)) {
			g.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
		}
		using (var font = new Font("Segoe UI Emoji", Height * 0.5f, GraphicsUnit.Pixel)) {
			var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
			g.DrawString(_busy && !_recording ? "\u2026" : _recording ? "\u25A0" : Glyph, font, Brushes.White, new RectangleF(0, 1, Width - 1, Height - 1), format);
		}
	}

	protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

	bool _pressed;
	Point _pressAt;

	protected override void OnMouseDown(MouseEventArgs e) {
		if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left) {
			_pressed = true;
			_pressAt = Cursor.Position;
			_dragging = e.Button == MouseButtons.Right;
			_dragFrom = e.Location;
			Capture = true;
		}
		base.OnMouseDown(e);
	}

	protected override void OnMouseMove(MouseEventArgs e) {
		if (_pressed && !_dragging) {
			var now = Cursor.Position;
			if (Math.Abs(now.X - _pressAt.X) > 4 || Math.Abs(now.Y - _pressAt.Y) > 4) _dragging = true;
		}
		if (_dragging) {
			var dx = e.X - _dragFrom.X;
			var dy = e.Y - _dragFrom.Y;
			if (dx != 0 || dy != 0) {
				Location = new Point(Location.X + dx, Location.Y + dy);
				if (Moving != null) Moving(dx, dy);
			}
		}
		base.OnMouseMove(e);
	}

	public Rectangle Client;

	protected override void OnMouseUp(MouseEventArgs e) {
		_pressed = false;
		if (_dragging) {
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

// "Оформление ВКЛ/ВЫКЛ" pill left of the ✨ button: formatting on Enter
// on or off in one click, always in sight while typing in AyuGram.
// Never takes the focus, so the message field stays active.
public class EnterToggle : Form {
	public Action Clicked;
	public const int BaseWidth = 104;
	public const int BaseHeight = 22;
	public const int ButtonSize = 22; // 🎙 and ✨, same height as the pill
	public const int GroupGap = 2; // px between pill, 🎙, ✨ and VoiceType at 100% scale
	public const int GroupStep = ButtonSize + GroupGap; // ✨ to 🎙
	bool _on = true;
	bool _hover;

	public EnterToggle() {
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		TopMost = true;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.FromArgb(36, 32, 48);
		DoubleBuffered = true;
		Cursor = Cursors.Hand;
		Size = new Size(BaseWidth, BaseHeight);
		var tip = new ToolTip();
		tip.SetToolTip(this, "Клик: всё AyuFancy включить или выключить\n(оформление по Enter, ✨ и 🎙)\nЗажми и тащи, чтобы переставить");
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

	public bool On {
		get { return _on; }
		set { if (_on != value) { _on = value; Invalidate(); } }
	}

	// Top centre of the AyuGram window, in the empty title strip, so it never
	// covers messages (ayu-fancy-pill-top-v1, Arthur 08.10: «its in my face»).
	// One block with 🎙 and ✨ (ayu-fancy-group-v1): the pill sits left of 🎙,
	// right and bottom = offset of ✨ from the client bottom right, 100% scale.
	public void Place(Rectangle client, float scale, int right, int bottom) {
		if (_dragging) return;
		_scale = scale;
		Client = client;
		var size = new Size((int)Math.Round(BaseWidth * scale), (int)Math.Round(BaseHeight * scale));
		var button = (int)Math.Round(ButtonSize * scale);
		var location = new Point(
			client.Right - (int)Math.Round((right + GroupStep + GroupGap) * scale) - button - size.Width,
			client.Bottom - (int)Math.Round(bottom * scale) - button + (button - size.Height) / 2);
		if (Size != size) {
			Size = size;
			// Rounded shape as the window region: no colour key, so no pink fringe.
			using (var path = Pill(new Rectangle(0, 0, size.Width, size.Height))) {
				Region = new Region(path);
			}
		}
		if (Location != location) Location = location;
	}

	static System.Drawing.Drawing2D.GraphicsPath Pill(Rectangle r) {
		var path = new System.Drawing.Drawing2D.GraphicsPath();
		var d = r.Height;
		path.AddArc(r.Left, r.Top, d, d, 90, 180);
		path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
		path.CloseFigure();
		return path;
	}

	protected override void OnPaint(PaintEventArgs e) {
		var g = e.Graphics;
		g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
		var fill = _on
			? (_hover ? Color.FromArgb(160, 105, 255) : Color.FromArgb(132, 82, 240))
			: (_hover ? Color.FromArgb(78, 78, 90) : Color.FromArgb(58, 58, 68));
		g.Clear(fill);
		var h = Height;
		// switch knob: right and white when on, left and grey when off
		var pad = Math.Max(3, h / 6);
		var knob = h - pad * 2;
		var knobX = _on ? Width - knob - pad - 1 : pad + 1;
		using (var brush = new SolidBrush(_on ? Color.White : Color.FromArgb(150, 150, 160))) {
			g.FillEllipse(brush, knobX, pad, knob, knob);
		}
		using (var font = new Font("Segoe UI Semibold", h * 0.45f, GraphicsUnit.Pixel))
		using (var brush = new SolidBrush(_on ? Color.White : Color.FromArgb(200, 200, 210))) {
			var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
			var text = _on ? "AyuFancy ВКЛ" : "AyuFancy ВЫКЛ";
			var area = _on ? new RectangleF(4, 0, Width - knob - 12, h) : new RectangleF(knob + 8, 0, Width - knob - 12, h);
			g.DrawString(text, font, brush, area, format);
		}
	}

	protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

	public Action<int, int> Moved; // new x, y from the client top left, 100% scale
	public Action<int, int> Moving; // screen px the pill just moved by while dragged
	public Rectangle Client;
	float _scale = 1f;
	bool _pressed;
	bool _dragging;
	Point _pressAt;
	Point _dragFrom;

	public bool Dragging {
		get { return _dragging; }
	}

	protected override void OnMouseDown(MouseEventArgs e) {
		if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) {
			_pressed = true;
			_pressAt = Cursor.Position;
			_dragFrom = e.Location;
			_dragging = e.Button == MouseButtons.Right;
			Capture = true;
		}
		base.OnMouseDown(e);
	}

	protected override void OnMouseMove(MouseEventArgs e) {
		if (_pressed && !_dragging) {
			var now = Cursor.Position;
			if (Math.Abs(now.X - _pressAt.X) > 4 || Math.Abs(now.Y - _pressAt.Y) > 4) _dragging = true;
		}
		if (_dragging) {
			var dx = e.X - _dragFrom.X;
			var dy = e.Y - _dragFrom.Y;
			if (dx != 0 || dy != 0) {
				Location = new Point(Location.X + dx, Location.Y + dy);
				if (Moving != null) Moving(dx, dy);
			}
		}
		base.OnMouseMove(e);
	}

	protected override void OnMouseUp(MouseEventArgs e) {
		var wasPressed = _pressed;
		_pressed = false;
		Capture = false;
		if (_dragging) {
			_dragging = false;
			if (Moved != null && _scale > 0) {
				Moved((int)Math.Round((Left - Client.Left) / _scale), (int)Math.Round((Top - Client.Top) / _scale));
			}
		} else if (wasPressed && e.Button == MouseButtons.Left && Clicked != null) {
			Clicked();
		}
		base.OnMouseUp(e);
	}
}

// The message field text through UI Automation: no clipboard, no keys,
// the caret and selection stay as they are.
public static class FieldReader {
	public static string Read(int pid) {
		try {
			var element = AutomationElement.FocusedElement;
			if (element == null || element.Current.ProcessId != pid) return null;
			object pattern;
			string value = null;
			if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) {
				value = ((ValuePattern)pattern).Current.Value;
			} else if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
				value = ((TextPattern)pattern).DocumentRange.GetText(-1);
			}
			return value == null ? null : value.Replace("\r\n", "\n").Replace('\r', '\n');
		} catch {
			return null;
		}
	}
}

// Formatting jobs by text: prefetched while typing, picked up on Enter.
public class Prefetcher {
	public class Job {
		public string Text;
		public ManualResetEvent Done = new ManualResetEvent(false);
		public Engines.Result Result;
		public DateTime Started = DateTime.Now;
	}

	readonly object _lock = new object();
	readonly List<Job> _jobs = new List<Job>();

	Job Find(string text) {
		return _jobs.FirstOrDefault(j => j.Text == text);
	}

	// Two background jobs at most: the newest text gets its own job even
	// while an older one for a half-typed message still runs (08.10: the
	// final text waited for that job, then Enter waited the full ~1.2 s).
	public void Prefetch(string text, Config config) {
		lock (_lock) {
			if (Find(text) != null || _jobs.Count(j => !j.Done.WaitOne(0)) >= 2) return;
		}
		Start(text, config);
	}

	public Job Start(string text, Config config) {
		Job job;
		lock (_lock) {
			job = Find(text);
			if (job != null) return job;
			job = new Job { Text = text };
			_jobs.Add(job);
			while (_jobs.Count > 6) _jobs.RemoveAt(0);
		}
		var thread = new Thread(() => {
			try {
				job.Result = Engines.Format(config, new Tagged(text, null));
			} catch (Exception e) {
				job.Result = new Engines.Result { Error = e.Message };
			}
			Log.Write("job " + (job.Result.Error.Length > 0 ? job.Result.Error : "ok") + " in " + job.Result.Milliseconds + " ms");
			job.Done.Set();
		});
		thread.IsBackground = true;
		thread.Start();
		return job;
	}
}

public class TrayApp : ApplicationContext {
	readonly NotifyIcon _tray;
	readonly Osd _osd;
	readonly Native.HookProc _proc;
	IntPtr _hook;
	uint _hookThread;
	readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();
	readonly SparkButton _button;
	readonly SparkButton _mic;
	volatile bool _recording;
	int _transcribing;
	IntPtr _micWindow;
	readonly EnterToggle _toggle;
	readonly ToolStripMenuItem _onEnter;
	readonly ToolStripMenuItem _master;
	bool _syncing;
	readonly System.Windows.Forms.Timer _follow;
	IntPtr _target;
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
		// Master switch (ayu-fancy-master-v1): off = no buttons, no Enter
		// formatting, no prefetch, no dictation; keys pass straight through.
		// Ctrl+Shift+F12 anywhere or a left click on the tray icon flips it.
		SaveSetting("enabled", true);
		SaveSetting("on_enter", true);
		_master = new ToolStripMenuItem("AyuFancy включён (Ctrl+Shift+F12)") { CheckOnClick = true, Checked = Config.Load().Enabled };
		_master.CheckedChanged += (s, e) => {
			if (_syncing) return;
			SetEnabled(_master.Checked);
		};
		_tray.ContextMenuStrip.Items.Add(_master);
		_tray.MouseClick += (s, e) => {
			if (e.Button == MouseButtons.Left) SetEnabled(!(_cfg ?? Config.Load()).Enabled);
		};
		if (!_master.Checked) {
			_tray.Icon = MakeIcon(false);
			_tray.Text = "AyuFancy выключен: Ctrl+Shift+F12";
		}
		_onEnter = new ToolStripMenuItem("Оформлять при отправке (Enter)") { CheckOnClick = true, Checked = Config.Load().OnEnter };
		_onEnter.CheckedChanged += (s, e) => {
			if (_syncing) return;
			SaveSetting("on_enter", _onEnter.Checked);
		};
		_tray.ContextMenuStrip.Items.Add(_onEnter);
		_tray.ContextMenuStrip.Items.Add("Настройки", null, (s, e) => OpenFile("ayu_fancy.json"));
		_tray.ContextMenuStrip.Items.Add("Кнопки и пилюлю на место", null, (s, e) => {
			SaveButtonPlace(14, 145);
		});
		_tray.ContextMenuStrip.Items.Add("Лог", null, (s, e) => OpenFile("ayu_fancy.log"));
		_tray.ContextMenuStrip.Items.Add("Выход", null, (s, e) => ExitThread());
		_button = new SparkButton();
		_mic = new SparkButton { Glyph = "\U0001F399" };
		_mic.Clicked = () => ToggleDictation(_target);
		_mic.Moving = (dx, dy) => { MoveBy(_button, dx, dy); MoveBy(_toggle, dx, dy); MoveVoiceType(dx, dy); };
		_button.Moving = (dx, dy) => { MoveBy(_mic, dx, dy); MoveBy(_toggle, dx, dy); MoveVoiceType(dx, dy); };
		_mic.Moved = (right, bottom) => SaveButtonPlace(right - EnterToggle.GroupStep, bottom);
		_button.Clicked = () => StartRun(_target, Trigger.Button);
		_button.Moved = SaveButtonPlace;
		// The pill is the one master switch (ayu-fancy-pill-master-v1).
		_toggle = new EnterToggle { On = _master.Checked };
		_toggle.Clicked = TogglePill;
		_toggle.Moving = (dx, dy) => { MoveBy(_mic, dx, dy); MoveBy(_button, dx, dy); MoveVoiceType(dx, dy); };
		// the whole block is saved as the place of ✨, worked out from the pill
		_toggle.Moved = (x, y) => {
			var c = _toggle.Client;
			var scale = _toggle.Width / (float)EnterToggle.BaseWidth;
			var right = (int)Math.Round((c.Right - _toggle.Right) / scale) - EnterToggle.GroupGap - EnterToggle.GroupStep - EnterToggle.ButtonSize;
			var bottom = (int)Math.Round((c.Bottom - _toggle.Bottom) / scale) - (EnterToggle.ButtonSize - EnterToggle.BaseHeight) / 2;
			SaveButtonPlace(right, bottom);
		};
		_follow = new System.Windows.Forms.Timer { Interval = 50 };
		_follow.Tick += (s, e) => Follow();
		_follow.Start();
		_cfg = Config.Load();
		var background = new Thread(Background);
		background.IsBackground = true;
		background.SetApartmentState(ApartmentState.MTA);
		background.Start();
		_proc = HookCallback;
		// The keyboard hook lives on its own thread with its own message loop.
		// On the UI thread, any stall there (a busy button, a slow device)
		// froze typing in the whole system, and Windows closed the hung
		// AyuFancy (06.10 18:01). Here nothing else runs on that thread.
		var hooks = new Thread(() => {
			_hookThread = Native.GetCurrentThreadId();
			_hook = Native.SetWindowsHookEx(13, _proc, Native.GetModuleHandle(null), 0);
			Log.Write("started, hook " + (_hook != IntPtr.Zero ? "ok" : "FAILED " + Marshal.GetLastWin32Error()) + " on its own thread");
			Native.MSG msg;
			while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
				Native.TranslateMessage(ref msg);
				Native.DispatchMessage(ref msg);
			}
		});
		hooks.IsBackground = true;
		hooks.Priority = ThreadPriority.AboveNormal;
		hooks.Start();
	}

	void SetEnabled(bool on) {
		if (!on && _recording) CancelDictation();
		SaveSetting("enabled", on);
		_syncing = true;
		_master.Checked = on;
		_syncing = false;
		_tray.Icon = MakeIcon(on);
		if (_toggle != null) _toggle.On = on;
		_tray.Text = on ? "AyuFancy: Ctrl+Shift+F в AyuGram" : "AyuFancy выключен: Ctrl+Shift+F12";
		Show(on ? "\u2728 AyuFancy включён" : "AyuFancy выключен (Ctrl+Shift+F12 вернуть)", IntPtr.Zero, 1800);
		Log.Write("master " + (on ? "on" : "off"));
	}

	static Icon MakeIcon(bool on = true) {
		using (var bitmap = new Bitmap(32, 32)) {
			using (var g = Graphics.FromImage(bitmap)) {
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
				g.Clear(Color.Transparent);
				using (var brush = new SolidBrush(on ? Color.FromArgb(150, 90, 255) : Color.FromArgb(110, 110, 120))) {
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
		ShowVoiceType(true);
		if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
		if (_hookThread != 0) Native.PostThreadMessage(_hookThread, 0x12, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
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

	// Runs for every key press in the whole system: memory reads only, no
	// files, no process lookups, no waiting. Anything slow here lags the
	// keyboard everywhere.
	IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam) {
		if (code >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104)) {
			var key = Marshal.ReadInt32(lParam);
			var flags = Marshal.ReadInt32(lParam, 8);
			var injected = (flags & 0x10) != 0;
			var config = _cfg;
			if (!injected && key == 0x7B // F12
				&& Native.Down(Native.VK_CONTROL)
				&& Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_MENU)) {
				try {
					_osd.BeginInvoke(new Action(() => SetEnabled(!(_cfg ?? Config.Load()).Enabled)));
				} catch {
				}
				return (IntPtr)1;
			}
			if (injected || config == null || !config.Enabled) {
				return Native.CallNextHookEx(_hook, code, wParam, lParam);
			}
			if (_recording && key == Native.VK_ESCAPE) {
				CancelDictation();
				return (IntPtr)1;
			}
			if (_recording && key == Native.VK_RETURN) {
				try { _mic.BeginInvoke(new Action(() => ToggleDictation(_micWindow))); } catch { }
				return (IntPtr)1;
			}
			if (key == 0x20 && config.Dictation
				&& Native.Down(Native.VK_CONTROL)
				&& Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_MENU)) {
				var window = Native.GetForegroundWindow();
				int pid;
				if (_recording || IsAppWindow(window, out pid)) {
					var target = _recording ? _micWindow : window;
					try { _mic.BeginInvoke(new Action(() => ToggleDictation(target))); } catch { }
					return (IntPtr)1;
				}
			}
			if (_enterRun && (key == Native.VK_ESCAPE || key == Native.VK_RETURN)) {
				_sendAsIs.Set(); // Esc or a second Enter: send as typed now
				return (IntPtr)1;
			}
			var plain = !Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_CONTROL)
				&& !Native.Down(Native.VK_MENU)
				&& !Native.Down(Native.VK_LWIN)
				&& !Native.Down(Native.VK_RWIN);
			if (key == Native.VK_RETURN && plain && config.OnEnter && _busy == 0) {
				var window = Native.GetForegroundWindow();
				int pid;
				if (IsAppWindow(window, out pid)) {
					StartRun(window, Trigger.Enter, pid);
					return (IntPtr)1;
				}
			}
			if (key == Native.VK_F
				&& Native.Down(Native.VK_CONTROL)
				&& Native.Down(Native.VK_SHIFT)
				&& !Native.Down(Native.VK_MENU)) {
				var window = Native.GetForegroundWindow();
				int pid;
				if (IsAppWindow(window, out pid)) {
					StartRun(window, Trigger.Hotkey, pid);
					return (IntPtr)1;
				}
			}
		}
		return Native.CallNextHookEx(_hook, code, wParam, lParam);
	}

	// Cached only: names are looked up by the background thread.
	bool IsAppWindow(IntPtr window, out int pid) {
		uint id;
		Native.GetWindowThreadProcessId(window, out id);
		pid = (int)id;
		var apps = _appPids;
		return apps != null && apps.Contains(pid);
	}

	Config CurrentConfig() {
		return _cfg ?? Config.Load();
	}

	volatile Config _cfg;
	volatile HashSet<int> _appPids = new HashSet<int>();

	// Background: settings, which processes are AyuGram, and prefetching the
	// formatted text while the user pauses typing.
	void Background() {
		var lastConfig = DateTime.MinValue;
		var lastApps = DateTime.MinValue;
		var lastWarm = DateTime.MinValue;
		string lastText = null;
		var changedAt = DateTime.Now;
		while (true) {
			try {
				Thread.Sleep(100);
				var now = DateTime.Now;
				if ((now - lastConfig).TotalSeconds >= 2 || _cfg == null) {
					_cfg = Config.Load();
					lastConfig = now;
				}
				var config = _cfg;
				if (config.Enabled && config.CodexServer && config.Engine != "groq" && (now - lastWarm).TotalSeconds >= 10) {
					lastWarm = now;
					var codex = Engines.FindCodex(config);
					if (codex != null) CodexServer.Warm(config, codex);
				}
				if (config.Enabled && config.Engine != "codex") {
					int warmPid;
					if (IsAppWindow(Native.GetForegroundWindow(), out warmPid)) Engines.WarmGroq(config);
				}
				if ((now - lastApps).TotalSeconds >= 3) {
					var pids = new HashSet<int>();
					foreach (var process in Process.GetProcesses()) {
						try {
							if (config.Apps.Contains(process.ProcessName.ToLowerInvariant())) pids.Add(process.Id);
						} catch {
						}
						process.Dispose();
					}
					_appPids = pids;
					lastApps = now;
				}
				if (!config.Enabled || !config.OnEnter || !config.Prefetch || _enterRun) continue;
				int pid;
				var window = Native.GetForegroundWindow();
				if (!IsAppWindow(window, out pid)) continue;
				var text = FieldReader.Read(pid);
				if (text == null) continue;
				if (text != lastText) {
					lastText = text;
					changedAt = now;
					continue;
				}
				if ((now - changedAt).TotalMilliseconds >= PrefetchPauseMs && Worth(text)) {
					_pre.Prefetch(text, config);
				}
			} catch (Exception e) {
				Log.Write("background: " + e.Message);
			}
		}
	}

	// Pause in typing before the text is formatted in the background: 450 ms
	// often missed the moment before Enter (ayu-fancy-prefetch-fast-v1).
	const int PrefetchPauseMs = 350; // 200 burned Groq's day limit by noon (08.10)

	static bool Worth(string text) {
		return HasWords(text, 2) && text.IndexOf('\uFFFC') < 0 && text.Length < 4000;
	}

	readonly Prefetcher _pre = new Prefetcher();

	public enum Trigger { Hotkey, Button, Enter }
	volatile bool _enterRun;
	readonly ManualResetEvent _sendAsIs = new ManualResetEvent(false);

	void StartRun(IntPtr window, Trigger trigger) {
		StartRun(window, trigger, 0);
	}

	void StartRun(IntPtr window, Trigger trigger, int pid) {
		if (window == IntPtr.Zero || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) {
			return;
		}
		if (trigger == Trigger.Enter) {
			_enterRun = true;
			_sendAsIs.Reset();
		}
		SetBusy(true);
		var thread = new Thread(() => {
			if (trigger == Trigger.Enter) RunEnter(window, pid);
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

	// Arthur 08.10: one obvious switch next to ✨ and 🎙 that definitely turns
	// everything off and on. On also turns formatting on Enter back on, so
	// «ВКЛ» on the pill always means Enter formats.
	void TogglePill() {
		var on = !CurrentConfig().Enabled;
		if (on && !CurrentConfig().OnEnter) {
			SaveSetting("on_enter", true);
			_syncing = true;
			_onEnter.Checked = true;
			_syncing = false;
		}
		SetEnabled(on);
	}

	// The pill: formatting on Enter on or off, same setting as the tray item.
	void ToggleEnter() {
		var on = !CurrentConfig().OnEnter;
		SaveSetting("on_enter", on);
		_syncing = true;
		_onEnter.Checked = on;
		_syncing = false;
		_toggle.On = on;
		Log.Write("on_enter " + (on ? "on" : "off") + " (pill)");
		Show(on ? "✨ Оформление по Enter включено" : "Оформление выключено: Enter отправляет как есть", _target, 1800);
	}

	// Keeps the button and the pill on the active AyuGram window, hides them otherwise.
	void Follow() {
		var config = _cfg;
		if (config == null) return;
		var foreground = Native.GetForegroundWindow();
		if (foreground == _button.Handle || foreground == _mic.Handle || foreground == _toggle.Handle || _button.Dragging || _mic.Dragging || _toggle.Dragging) {
			return;
		}
		var window = foreground != IntPtr.Zero ? Native.GetAncestor(foreground, 2) : IntPtr.Zero; // GA_ROOT
		int appPid;
		if (_master.Checked != config.Enabled) { // ayu_fancy.json edited by hand
			_syncing = true;
			_master.Checked = config.Enabled;
			_syncing = false;
			_tray.Icon = MakeIcon(config.Enabled);
		}
		_toggle.On = config.Enabled;
		// the pill stays in sight when everything is off, to turn it back on
		var show = window != IntPtr.Zero
			&& IsAppWindow(window, out appPid)
			&& Native.IsWindowVisible(window)
			&& !Native.IsIconic(window);
		if (!show) {
			ShowVoiceType(true);
			if (_button.Visible && _busy == 0) _button.Hide();
			if (_mic.Visible) _mic.Hide(); // красная кнопка записи не торчит поверх чужих окон
			if (_toggle.Visible) _toggle.Hide();
			return;
		}
		Native.RECT rect;
		if (!Native.GetClientRect(window, out rect) || rect.Right < 300 || rect.Bottom < 200) {
			ShowVoiceType(true);
			if (_button.Visible) _button.Hide();
			if (_mic.Visible) _mic.Hide(); // красная кнопка записи не торчит поверх чужих окон
			if (_toggle.Visible) _toggle.Hide();
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
		var right = config.ButtonRight;
		var bottom = config.ButtonBottom;
		// VoiceType pill sits right of ✨; dragging it drags the whole block
		var vt = FindVoiceType();
		Native.RECT vtRect = new Native.RECT();
		var vtWidth = 0;
		if (vt != IntPtr.Zero && Native.GetWindowRect(vt, out vtRect)) {
			vtWidth = vtRect.Right - vtRect.Left;
			var at = new Point(vtRect.Left, vtRect.Top);
			if (_vtSet.HasValue && _vtSet.Value != at) {
				var button = (int)Math.Round(EnterToggle.ButtonSize * scale);
				var vtHeight = vtRect.Bottom - vtRect.Top;
				var r = (int)Math.Round((client.Right - vtRect.Left) / scale) + EnterToggle.GroupGap;
				var b = (int)Math.Round((client.Bottom - (vtRect.Top + (vtHeight - button) / 2) - button) / scale);
				_vtGroup = new Point(r, b);
				_vtSet = at;
			}
			if (_vtGroup.HasValue) {
				right = _vtGroup.Value.X;
				bottom = _vtGroup.Value.Y;
				if ((Native.GetAsyncKeyState(0x01) & 0x8000) == 0) { // left button up: drag done
					_vtGroup = null;
					SaveButtonPlace(right, bottom);
				}
			}
		} else {
			vt = IntPtr.Zero;
		}
		// the block [pill][🎙][✨][VoiceType] stays inside the window whole
		var vtUnits = vt != IntPtr.Zero ? EnterToggle.GroupGap + (int)Math.Ceiling(vtWidth / scale) : 0;
		var groupWidth = EnterToggle.BaseWidth + EnterToggle.GroupGap + EnterToggle.GroupStep + EnterToggle.ButtonSize + vtUnits;
		right = Math.Max(vtUnits, Math.Min(right, (int)(rect.Right / scale) - groupWidth + vtUnits));
		bottom = Math.Max(0, Math.Min(bottom, (int)(rect.Bottom / scale) - EnterToggle.ButtonSize));
		if (vt != IntPtr.Zero && !_vtGroup.HasValue) {
			var button = (int)Math.Round(EnterToggle.ButtonSize * scale);
			var want = new Point(
				client.Right - (int)Math.Round(right * scale) + (int)Math.Round(EnterToggle.GroupGap * scale),
				client.Bottom - (int)Math.Round(bottom * scale) - button + (button - (vtRect.Bottom - vtRect.Top)) / 2);
			if (want != new Point(vtRect.Left, vtRect.Top)) {
				Native.SetWindowPos(vt, IntPtr.Zero, want.X, want.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE, NOZORDER, NOACTIVATE
			}
			_vtSet = want;
		}
		// Folded: only 🎙 and ✨ (or the pill when AyuFancy is off). The mouse over them
		// opens all four, they fold again 0.7 s after the mouse leaves.
		var px = (int)Math.Round(EnterToggle.ButtonSize * scale);
		var sparkRight = client.Right - (int)Math.Round(right * scale);
		var sparkBottom = client.Bottom - (int)Math.Round(bottom * scale);
		var anchorIsSpark = config.Enabled && config.Button;
		var full = Rectangle.FromLTRB(
			sparkRight - (int)Math.Round((EnterToggle.BaseWidth + EnterToggle.GroupGap + EnterToggle.GroupStep + EnterToggle.ButtonSize) * scale),
			sparkBottom - px,
			sparkRight + (int)Math.Round(vtUnits * scale),
			sparkBottom);
		// folded = 🎙 and ✨ side by side (Arthur 08.10: «what about the voice dictation, where that go»)
		var micToo = config.Dictation ? (int)Math.Round(EnterToggle.GroupStep * scale) : 0;
		var small = anchorIsSpark
			? new Rectangle(sparkRight - px - micToo, sparkBottom - px, px + micToo, px)
			: Rectangle.FromLTRB(full.Left, full.Top, full.Left + (int)Math.Round(EnterToggle.BaseWidth * scale), full.Bottom);
		var now = DateTime.Now;
		var area = _open ? full : small;
		area.Inflate((int)Math.Round(6 * scale), (int)Math.Round(6 * scale));
		if (area.Contains(Cursor.Position) || _vtGroup.HasValue) _hoverAt = now;
		_open = !config.Compact || now - _hoverAt < TimeSpan.FromMilliseconds(700);
		// ayu-fancy-off-hidden-v1: AyuFancy off = nothing over AyuGram (Arthur 10.10: «in my face»);
		// back on from the tray icon or Ctrl+Shift+F12
		var pill = config.Enabled && (_open || !anchorIsSpark);
		// always left of 🎙's place, so the pill never jumps
		_toggle.Place(client, scale, right, bottom);
		if (pill && !_toggle.Visible) _toggle.Show();
		if (!pill && _toggle.Visible) _toggle.Hide();
		ShowVoiceType(_open);
		if (!config.Enabled) {
			if (_button.Visible && _busy == 0) _button.Hide();
			if (_mic.Visible && !_recording) _mic.Hide();
			return;
		}
		if (config.Button) {
			_button.Client = client;
			_button.Place(client, scale, right, bottom);
			if (!_button.Visible) _button.Show();
		} else if (_button.Visible && _busy == 0) {
			_button.Hide();
		}
		if (config.Dictation) {
			_mic.Client = client;
			_mic.Place(client, scale, right + EnterToggle.GroupStep, bottom);
			if (!_mic.Visible) _mic.Show();
		} else if (_mic.Visible && !_recording) {
			_mic.Hide();
		}
		if (_onEnter.Checked != config.OnEnter) { // ayu_fancy.json edited by hand
			_syncing = true;
			_onEnter.Checked = config.OnEnter;
			_syncing = false;
		}
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
			_cfg = Config.Load();
		} catch (Exception e) {
			Log.Write("setting not saved: " + e.Message);
		}
	}

	// VoiceType (D:\VoiceType) pill: a Tk window of a pythonw running voicetype.py.
	IntPtr _vt;
	DateTime _vtLookup;
	Point? _vtSet; // where AyuFancy put it last; anything else = the user dragged it
	Point? _vtGroup; // right, bottom of ✨ while VoiceType is dragged, saved on release

	IntPtr FindVoiceType() {
		if (_vt != IntPtr.Zero && Native.IsWindow(_vt)) return _vt;
		if (DateTime.Now - _vtLookup < TimeSpan.FromSeconds(5)) return IntPtr.Zero;
		_vtLookup = DateTime.Now;
		_vt = IntPtr.Zero;
		_vtSet = null;
		var pids = new HashSet<uint>();
		try {
			using (var search = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'")) {
				foreach (System.Management.ManagementObject process in search.Get()) {
					var line = process["CommandLine"] as string;
					if (line != null && line.IndexOf("voicetype.py", StringComparison.OrdinalIgnoreCase) >= 0) pids.Add((uint)process["ProcessId"]);
				}
			}
		} catch {
		}
		if (pids.Count == 0) return IntPtr.Zero;
		var found = IntPtr.Zero;
		Native.EnumWindows((w, l) => {
			uint pid;
			Native.GetWindowThreadProcessId(w, out pid);
			if (!pids.Contains(pid)) return true;
			var name = new StringBuilder(32);
			Native.GetClassName(w, name, 32);
			Native.RECT r;
			if (name.ToString() == "TkTopLevel" && Native.GetWindowRect(w, out r) && r.Bottom - r.Top < 80 && r.Bottom - r.Top > 10 && r.Right - r.Left > 60) {
				found = w;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		_vt = found;
		_vtHidden = found != IntPtr.Zero && !Native.IsWindowVisible(found); // a crash left it hidden
		return found;
	}

	bool _vtHidden;
	bool _open;
	DateTime _hoverAt;

	// Hidden while the block is folded; shown again outside AyuGram and on exit.
	void ShowVoiceType(bool show) {
		if (_vt == IntPtr.Zero || !Native.IsWindow(_vt)) return;
		if (show == !_vtHidden) return;
		Native.ShowWindow(_vt, show ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
		_vtHidden = !show;
	}

	void MoveVoiceType(int dx, int dy) {
		Native.RECT r;
		if (_vt == IntPtr.Zero || !Native.GetWindowRect(_vt, out r)) return;
		var at = new Point(r.Left + dx, r.Top + dy);
		Native.SetWindowPos(_vt, IntPtr.Zero, at.X, at.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
		_vtSet = at;
	}

	static void MoveBy(Form form, int dx, int dy) {
		if (form.Visible) form.Location = new Point(form.Location.X + dx, form.Location.Y + dy);
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
			_cfg = Config.Load();
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

	// Click on the mic (or Ctrl+Shift+Space): start; again: stop. Arthur
	// 08.10: words must appear in the field while he speaks. Every phrase
	// (a pause of ~0.45 s, or 9 s of talk) goes to Whisper at once and lands
	// at the caret; then the first Enter only fixes the text, the second sends.
	LiveMic _live;
	volatile bool _dictationCancel;
	volatile bool _dictated;
	volatile string _sendNext;

	void ToggleDictation(IntPtr window) {
		if (_recording) {
			_recording = false; // the live worker finishes the last phrase
			_mic.Recording = false;
			return;
		}
		if (window == IntPtr.Zero || Interlocked.CompareExchange(ref _transcribing, 1, 0) != 0) return;
		var live = new LiveMic();
		try {
			live.Start();
		} catch (Exception e) {
			Log.Write("mic: " + e.Message);
			Show("\U0001F399 " + e.Message, window, 5000);
			Interlocked.Exchange(ref _transcribing, 0);
			return;
		}
		_live = live;
		Log.Write("dictation: start");
		Engines.WarmGroq(CurrentConfig()); // the first phrase took 6.5 s on a cold connection
		_micWindow = window;
		_dictationCancel = false;
		_recording = true;
		_mic.Recording = true;
		Show("\U0001F399 Говори, слова появятся сами\u2026  \U0001F399, Enter или Ctrl+Shift+Пробел: готово, Esc: стоп", window, 0);
		var worker = new Thread(() => LiveDictation(window, live));
		worker.SetApartmentState(ApartmentState.STA);
		worker.IsBackground = true;
		worker.Start();
	}

	void CancelDictation() {
		_dictationCancel = true;
		_recording = false;
		try { _mic.BeginInvoke(new Action(() => { _mic.Recording = false; })); } catch { }
		Show("\U0001F399 Остановлено", _micWindow, 1500);
	}

	// Loudness of 50 ms frames (800 samples).
	public static double FrameRms(byte[] pcm, int frame) {
		double sum = 0;
		var from = frame * 1600;
		for (var i = from; i + 1 < from + 1600 && i + 1 < pcm.Length; i += 2) {
			var v = (short)(pcm[i] | (pcm[i + 1] << 8));
			sum += v * (double)v;
		}
		return Math.Sqrt(sum / 800);
	}

	// Where the phrase being spoken ends (a frame), or -1 while it goes on.
	public static int FindCut(byte[] pcm, int frames, ref int segment) {
		if (frames - segment < 12) return -1;
		// quiet = clearly below the loud part of this phrase
		var peak = 0.0;
		for (var f = segment; f < frames; ++f) peak = Math.Max(peak, FrameRms(pcm, f));
		var quiet = Math.Max(90, peak * 0.12); // 12:07: a quiet mic peaked at ~380
		var silent = 0;
		for (var f = frames - 1; f >= segment && FrameRms(pcm, f) < quiet; --f) ++silent;
		// a pause after speech; 0.75 s: shorter phrases came out garbled (08.10)
		// 08.10 evening: 0.5 s and at most 4 s, Arthur talks without long pauses
		if (silent >= 10 && frames - silent - segment >= 10) return frames - silent + 4;
		if (frames - segment >= 80) { // 4 s without a pause: cut at the quietest spot of the last 1.5 s
			var cut = frames - 1;
			for (var f = frames - 30; f < frames; ++f) if (FrameRms(pcm, f) < FrameRms(pcm, cut)) cut = f;
			return cut;
		}
		if (silent >= 40) segment = frames - 10; // long silence: skip it
		return -1;
	}

	// One dictated phrase in Russian (or DictationLanguage): natural, with
	// the commas it needs, and Arthur's rules: no ? ! : ; and no period at
	// the end. Any failure: the phrase goes in as spoken.
	public static string ToLanguage(Config config, string phrase, string before) {
		var language = config.DictationLanguage == "ru" ? "Russian" : config.DictationLanguage;
		var prompt = "Turn this dictated phrase into natural " + language + " text, as a native speaker would type it in a Telegram chat. "
			+ "If it is in another language, translate it; if it is already " + language + ", only fix recognition errors. "
			+ "Keep the meaning, names, brands, slang and profanity. Commas only where they are really needed. "
			+ "Never use ? ! : ; and do not end with a period. The phrase continues the text before it: do not repeat that text.\n"
			+ (before.Trim().Length > 0 ? "Text before: " + (before.Length > 400 ? before.Substring(before.Length - 400) : before).Trim() + "\n" : "")
			+ "Phrase:\n<<<\n" + phrase + "\n>>>\nAnswer with the " + language + " text only.";
		// Groq first (better Russian than Google in tests); Google Translate
		// when every Groq model is on its day limit (08.10 11:53 the phrases
		// came raw, lowercase and without punctuation).
		try {
			Engines.EffortOverride = "low";
			var answer = Engines.AskChat(config, config.Model, prompt);
			if (answer.Error.Length > 0 || string.IsNullOrWhiteSpace(answer.Text)) {
				Log.Write("dictation translate: " + answer.Error + ", Google instead");
				var google = GoogleTranslate(phrase, config.DictationLanguage);
				return google != null ? CleanMarks(google) : phrase;
			}
			return CleanMarks(answer.Text.Trim().Trim('<', '>').Trim());
		} catch (Exception e) {
			Log.Write("dictation translate: " + e.Message);
			return phrase;
		} finally {
			Engines.EffortOverride = null;
		}
	}

	static string GoogleTranslate(string text, string language) {
		try {
			Engines.TuneNet();
			var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&tl=" + Uri.EscapeDataString(language) + "&q=" + Uri.EscapeDataString(text);
			var request = (HttpWebRequest)WebRequest.Create(url);
			request.KeepAlive = true;
			request.Timeout = 4000;
			string body;
			using (var response = (HttpWebResponse)request.GetResponse())
			using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) body = reader.ReadToEnd();
			var json = new JavaScriptSerializer().DeserializeObject(body) as object[];
			var parts = json != null ? json[0] as object[] : null;
			if (parts == null) return null;
			var result = new StringBuilder();
			foreach (var part in parts) {
				var piece = part as object[];
				if (piece != null && piece.Length > 0 && piece[0] is string) result.Append((string)piece[0]);
			}
			return result.Length > 0 ? result.ToString() : null;
		} catch (Exception e) {
			Log.Write("google translate: " + e.Message);
			return null;
		}
	}

	// Arthur's rules: no ? ! : ; (times like 19:00 stay), no period at the end.
	static string CleanMarks(string text) {
		try {
			text = Regex.Replace(text, "[?!;]", "");
			text = Regex.Replace(text, "(?<!\\d):|:(?!\\d)", "");
			text = Regex.Replace(text, "(?<!\\.)\\.\\s*$", "");
			return text.Trim();
		} catch {
			return text;
		}
	}

	// Whisper makes these up on silence.
	static readonly Regex Hallucination = new Regex("^(продолжение следует|субтитры|редактор субтитров|спасибо за просмотр|thank you|thanks for watching|subtitles by|amara|you)[\\s\\S]{0,60}$", RegexOptions.IgnoreCase);

	void LiveDictation(IntPtr window, LiveMic live) {
		var config = CurrentConfig();
		var watch = Stopwatch.StartNew();
		var dictated = new StringBuilder();
		var pending = new StringBuilder();
		string savedClipboard = null;
		var clipboardTaken = false;
		var segment = 0; // first frame of the phrase being spoken
		var phrases = 0;
		var models = new List<string> { config.WhisperModel };
		if (config.WhisperModel != "whisper-large-v3-turbo") models.Add("whisper-large-v3-turbo");
		var restUntil = new Dictionary<string, DateTime>();
		Func<byte[], int, int, bool> send = (pcm, from, till) => {
			// a phrase with too little loud sound is noise or breath
			var loud = 0;
			var floor = 0.0;
			for (var f = from; f < till; ++f) floor += FrameRms(pcm, f);
			floor = Math.Max(110, floor / Math.Max(1, till - from) * 0.6);
			for (var f = from; f < till; ++f) if (FrameRms(pcm, f) > floor) ++loud;
			if (loud < 4) { Log.Write("dictation: phrase " + (till - from) / 20.0 + " s skipped as noise, loud " + loud + ", floor " + (int)floor); return true; }
			foreach (var model in models) {
				DateTime until;
				if (restUntil.TryGetValue(model, out until) && DateTime.Now < until) continue;
				int status;
				var answer = Whisper.Transcribe(config, Recorder.Wav(pcm, from * 1600, Math.Min(pcm.Length, till * 1600) - from * 1600), model, dictated.ToString(), out status);
				if (status == 429) { restUntil[model] = DateTime.Now.AddSeconds(20); continue; }
				if (answer.Error.Length > 0) { Show("\U0001F399 " + answer.Error, window, 4000); return false; }
				var text = answer.Text.Trim();
				if (text.Length > 0 && !Hallucination.IsMatch(text) && config.DictationLanguage.Length > 0) text = ToLanguage(config, text, dictated.ToString());
				Log.Write("dictation: phrase " + (till - from) / 20.0 + " s, loud " + loud + ", " + model + ": " + text.Length + " chars" + (Hallucination.IsMatch(text) ? " (junk)" : ""));
				if (text.Length == 0 || Hallucination.IsMatch(text)) return true;
				if (pending.Length > 0 || dictated.Length > 0) pending.Append(' ');
				pending.Append(text);
				dictated.Append(' ').Append(text);
				++phrases;
				return true;
			}
			Show("\U0001F399 Whisper на лимите, подожди минуту", window, 4000);
			return false;
		};
		Action flush = () => {
			if (pending.Length == 0) return;
			if (Native.GetForegroundWindow() != window) return; // back in AyuGram: goes in then
			uint id;
			Native.GetWindowThreadProcessId(window, out id);
			var before = FieldReader.Read((int)id);
			var text = pending.ToString().TrimStart();
			if (!string.IsNullOrEmpty(before) && !char.IsWhiteSpace(before[before.Length - 1])) text = " " + text;
			if (!clipboardTaken) {
				savedClipboard = Retry<string>(() => Clipboard.ContainsText() ? Clipboard.GetText() : null, null);
				clipboardTaken = true;
			}
			RetryDo(() => Clipboard.SetText(text));
			Native.Press(Native.VK_CONTROL, 'V');
			Thread.Sleep(150);
			pending.Length = 0;
		};
		try {
			while (_recording) {
				Thread.Sleep(100);
				var pcm = live.Snapshot();
				var frames = pcm.Length / 1600;
				var cut = FindCut(pcm, frames, ref segment);
				if (cut < 0) continue;
				if (send(pcm, segment, cut)) segment = cut;
				flush();
			}
			live.Stop();
			if (!_dictationCancel) {
				var pcm = live.Snapshot();
				var frames = (pcm.Length + 1599) / 1600;
				if (frames - segment >= 6) {
					Show("\U0001F399 Дописываю\u2026", window, 0);
					send(pcm, segment, frames);
				}
			}
			if (pending.Length > 0 && Native.GetForegroundWindow() != window) {
				Native.SetForegroundWindow(window);
				Thread.Sleep(150);
			}
			flush();
			if (phrases == 0) {
				Log.Write("dictation: no phrases, " + live.Snapshot().Length / 32000.0 + " s recorded");
				if (!_dictationCancel) Show("\U0001F399 Ничего не расслышал", window, 2500);
				return;
			}
			_dictated = true;
			Log.Write(string.Format("dictation: live, {0} phrases, {1:0.0} s", phrases, watch.ElapsedMilliseconds / 1000.0));
			Show("\U0001F399 Готово. Enter: поправить текст, ещё Enter: отправить", window, 3000);
		} catch (Exception e) {
			Log.Write("dictation: " + e);
			Show("\U0001F399 Ошибка: " + e.Message, window, 5000);
		} finally {
			try { live.Stop(); } catch { }
			if (clipboardTaken) {
				var text = savedClipboard;
				Thread.Sleep(200);
				RetryDo(() => { if (text != null) Clipboard.SetText(text); });
			}
			_recording = false;
			try { _mic.BeginInvoke(new Action(() => { _mic.Recording = false; })); } catch { }
			Interlocked.Exchange(ref _transcribing, 0);
		}
	}

	// Enter in AyuGram: the message goes out within EnterBudgetMs, formatted
	// if the formatting is ready by then (usually prefetched while typing),
	// as typed otherwise. The field is not touched while waiting.
	void RunEnter(IntPtr window, int pid) {
		string saved = null;
		var savedTaken = false;
		var watch = Stopwatch.StartNew();
		try {
			var config = CurrentConfig();
			var text = FieldReader.Read(pid);
			var viaUia = text != null;
			if (!viaUia) {
				// No UI Automation: copy the field, the old way.
				saved = Retry<string>(() => Clipboard.ContainsText() ? Clipboard.GetText() : null, null);
				savedTaken = true;
				var copied = CopyFromField(true);
				text = copied != null ? copied.Text : null;
			}
			var sendNext = _sendNext;
			_sendNext = null;
			var preview = _dictated;
			_dictated = false;
			if (sendNext != null && text == sendNext) {
				Native.Tap(Native.VK_RETURN); // second Enter after dictation: send
				return;
			}
			if (text == null || !Worth(text)) {
				Native.Tap(Native.VK_RETURN);
				return;
			}
			var job = _pre.Start(text, config);
			var shown = false;
			while (!job.Done.WaitOne(25)) {
				if (_sendAsIs.WaitOne(0) || watch.ElapsedMilliseconds >= config.EnterBudgetMs - 150) break;
				if (!shown && watch.ElapsedMilliseconds > 500) {
					Show("\u2728 Оформляю\u2026  Enter или Esc: отправить как есть", window, 0);
					shown = true;
				}
			}
			var result = job.Done.WaitOne(0) ? job.Result : null;
			if (Native.GetForegroundWindow() != window) {
				Show("\u2728 Окно сменилось, не отправил", window, 3000);
				return;
			}
			if (viaUia && FieldReader.Read(pid) != text) {
				Show("\u2728 Текст поменялся, жми Enter ещё раз", window, 3000);
				return;
			}
			var ok = result != null && result.Error.Length == 0 && result.Text != null;
			Log.Write(string.Format("enter: uia {0}, waited {1} ms, job {2}, {3}", viaUia ? "ok" : "no", watch.ElapsedMilliseconds,
				job.Done.WaitOne(0) ? "done" : "running", result == null ? "not ready" : (result.Error.Length > 0 ? result.Error : (result.Engine + " " + result.Milliseconds + " ms"))));
			if (!ok && preview) {
				_sendNext = text;
				Show("\u2728 Не поправил (" + (result == null ? "не успел" : result.Error) + "). Enter: отправить как есть", window, 3000);
				return;
			}
			if (!ok) {
				Native.Tap(Native.VK_RETURN);
				if (true) {
					var reason = _sendAsIs.WaitOne(0) ? "по твоей команде" : result == null ? ("не успел за " + (config.EnterBudgetMs / 1000.0).ToString("0.#") + " с") : result.Error;
					Show("\u2728 Отправил как есть: " + reason, window, 2500);
				}
				return;
			}
			if (result.Text.Text == text && result.Text.Tags.Count == 0 && preview) {
				_sendNext = text;
				Show("\u2728 Тут нечего править. Enter: отправить", window, 2000);
				return;
			}
			if (result.Text.Text == text && result.Text.Tags.Count == 0) {
				Native.Tap(Native.VK_RETURN); // nothing to change
				Show("\u2728 Тут нечего править", window, 1200);
				return;
			}
			if (!savedTaken) {
				saved = Retry<string>(() => Clipboard.ContainsText() ? Clipboard.GetText() : null, null);
				savedTaken = true;
			}
			WriteClipboard(result.Text);
			Native.Press(Native.VK_CONTROL, 'A');
			Thread.Sleep(30);
			Native.Press(Native.VK_CONTROL, 'V');
			Thread.Sleep(120);
			if (preview) {
				Thread.Sleep(150);
				_sendNext = viaUia ? FieldReader.Read(pid) : null;
				Show("\u2728 Поправил. Enter: отправить", window, 2500);
				return;
			}
			Native.Tap(Native.VK_RETURN);
			Show(string.Format("\u2728 {0:0.0} с", watch.ElapsedMilliseconds / 1000.0), window, 1200);
			Thread.Sleep(600);
		} catch (Exception e) {
			Log.Write("enter failed: " + e);
			try { Native.Tap(Native.VK_RETURN); } catch { }
		} finally {
			if (savedTaken) {
				var text = saved;
				RetryDo(() => { if (text != null) Clipboard.SetText(text); });
			}
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
		var config = Config.Load();
		// The warm server starts once per app run: keep that out of the timing.
		var codex = config.Engine != "groq" && config.CodexServer ? Engines.FindCodex(config) : null;
		if (codex != null) CodexServer.Warm(config, codex);
		var result = Engines.Format(config, new Tagged(text, null));
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

	// --livetest <in.wav|mic:N> <out.txt>: phrase splitting of live dictation
	// on a 16 kHz mono WAV fed in 100 ms steps (or N s of microphone), each
	// phrase through Whisper, no field and no keys touched.
	static int LiveTest(string input, string output) {
		var report = new StringBuilder();
		byte[] pcm;
		if (input.StartsWith("mic:")) {
			var mic = new LiveMic();
			mic.Start();
			Thread.Sleep(int.Parse(input.Substring(4)) * 1000);
			mic.Stop();
			pcm = mic.Snapshot();
		} else {
			var wav = File.ReadAllBytes(input);
			pcm = new byte[wav.Length - 44];
			Array.Copy(wav, 44, pcm, 0, pcm.Length);
		}
		report.AppendLine("seconds of sound: " + (pcm.Length / 32000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
		var peak = 0.0;
		for (var f = 0; f < pcm.Length / 1600; ++f) peak = Math.Max(peak, TrayApp.FrameRms(pcm, f));
		report.AppendLine("peak rms: " + (int)peak);
		var config = Config.Load();
		var segment = 0;
		var context = "";
		Action<int, int, int> send = (from, till, at) => {
			int status;
			var watch = Stopwatch.StartNew();
			var answer = Whisper.Transcribe(config, Recorder.Wav(pcm, from * 1600, Math.Min(pcm.Length, till * 1600) - from * 1600), config.WhisperModel, context, out status);
			var whisperMs = watch.ElapsedMilliseconds;
			var ru = config.DictationLanguage.Length > 0 && answer.Text.Length > 0 ? TrayApp.ToLanguage(config, answer.Text, context) : answer.Text;
			context += " " + ru;
			report.AppendLine(string.Format("phrase {0:0.00}-{1:0.00} s, cut seen at {2:0.0} s, whisper {3} ms, total {4} ms: {5} => {6}{7}", from / 20.0, till / 20.0, at / 20.0, whisperMs, watch.ElapsedMilliseconds, answer.Text, ru, answer.Error));
		};
		for (var frames = 2; frames <= pcm.Length / 1600; frames += 2) {
			var cut = TrayApp.FindCut(pcm, frames, ref segment);
			if (cut < 0) continue;
			send(segment, cut, frames);
			segment = cut;
		}
		if (pcm.Length / 1600 - segment >= 6) send(segment, (pcm.Length + 1599) / 1600, pcm.Length / 1600);
		File.WriteAllText(output, report.ToString(), new UTF8Encoding(false));
		return 0;
	}

	// Autostart runs "AyuFancy.exe --guard": it starts AyuFancy and starts it
	// again if it dies. On 06.10 at 18:01 Windows closed a hung AyuFancy and
	// ✨, 🎙 and the pill were gone until the next logon. «Выход» in the tray
	// ends with code 0, then the guard stops too.
	static int Guard() {
		bool created;
		using (var mutex = new Mutex(true, "Local\\AyuFancyGuard", out created)) {
			if (!created) return 0;
			var exe = Application.ExecutablePath;
			var starts = new List<DateTime>();
			while (true) {
				starts.Add(DateTime.Now);
				starts.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 5);
				Process child;
				try {
					child = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
				} catch (Exception e) {
					Log.Write("guard: cannot start: " + e.Message);
					return 1;
				}
				child.WaitForExit();
				var code = child.ExitCode;
				if (code == 0) {
					Log.Write("guard: AyuFancy closed normally, guard stops");
					return 0;
				}
				// crashing again and again: slow down instead of spinning
				var wait = starts.Count >= 5 ? 60000 : 2000;
				Log.Write("guard: AyuFancy died (code " + code + "), restart in " + wait / 1000 + " s");
				Thread.Sleep(wait);
			}
		}
	}

	[STAThread]
	public static int Main(string[] args) {
		if (args.Length == 1 && args[0] == "--guard") {
			return Guard();
		}
		if (args.Length == 3 && args[0] == "--livetest") return LiveTest(args[1], args[2]);
		if (args.Length == 3 && args[0] == "--selftest") {
			return SelfTest(args[1], args[2]);
		}
		if (args.Length == 2 && args[0] == "--whisper-test") {
			var answer = Whisper.Transcribe(Config.Load(), Recorder.SilenceWav());
			File.WriteAllText(args[1], "error: " + answer.Error + "\ntext: " + answer.Text + "\n", new UTF8Encoding(false));
			return answer.Error.Length == 0 ? 0 : 1;
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
