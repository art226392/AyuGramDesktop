// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#include "ayu/features/fancy_format/fancy_format.h"

#include "ayu/features/fancy_format/fancy_format_text.h"
#include "main/session/session_show.h"
#include "ui/widgets/fields/input_field.h"
#include "logs.h"
#include "settings.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QDir>
#include <QtCore/QFile>
#include <QtCore/QFileInfo>
#include <QtCore/QJsonArray>
#include <QtCore/QJsonDocument>
#include <QtCore/QJsonObject>
#include <QtCore/QPointer>
#include <QtCore/QProcess>
#include <QtCore/QProcessEnvironment>
#include <QtCore/QRegularExpression>
#include <QtCore/QStandardPaths>
#include <QtCore/QTemporaryDir>
#include <QtCore/QTimer>
#include <QtCore/QUrl>
#include <QtGui/QAction>
#include <QtGui/QKeyEvent>
#include <QtGui/QKeySequence>
#include <QtNetwork/QNetworkAccessManager>
#include <QtNetwork/QNetworkReply>
#include <QtNetwork/QNetworkRequest>
#include <QtWidgets/QMenu>
#include <QtWidgets/QTextEdit>

#include <optional>
#include <set>

namespace AyuFeatures::FancyFormat {
namespace {

constexpr auto kCodexTimeoutMs = 120 * 1000;
constexpr auto kHttpTimeoutMs = 90 * 1000;
constexpr auto kFallbackModel = "llama-3.3-70b-versatile";
#ifdef Q_OS_WIN
constexpr auto kCreateNoWindow = 0x08000000; // CREATE_NO_WINDOW
#endif // Q_OS_WIN

struct Config {
	bool enabled = true;
	QString engine = u"auto"_q;
	QString codexPath;
	QString codexModel;
	QString codexEffort = u"low"_q;
	bool codexUserConfig = false;
	QString key;
	QString baseUrl = u"https://api.groq.com/openai/v1"_q;
	QString model = u"openai/gpt-oss-120b"_q;
	QString style;
};

struct Answer {
	QString text;
	QString error;
	QString engine;
};
using AnswerCallback = std::function<void(Answer)>;

std::set<Ui::InputField*> Busy;

[[nodiscard]] QString ReadString(
		const QJsonObject &object,
		const char *name,
		const QString &fallback) {
	const auto value = object.value(QLatin1String(name));
	return value.isString() ? value.toString().trimmed() : fallback;
}

[[nodiscard]] std::optional<QJsonObject> ReadJson(const QString &name) {
	const auto paths = {
		cExeDir() + name,
		cWorkingDir() + u"tdata/"_q + name,
	};
	for (const auto &path : paths) {
		auto file = QFile(path);
		if (!file.open(QIODevice::ReadOnly)) {
			continue;
		}
		auto error = QJsonParseError();
		const auto document = QJsonDocument::fromJson(file.readAll(), &error);
		if (error.error != QJsonParseError::NoError || !document.isObject()) {
			LOG(("AyuFancy: bad config %1: %2").arg(path, error.errorString()));
			continue;
		}
		return document.object();
	}
	return std::nullopt;
}

[[nodiscard]] Config LoadConfig() {
	auto result = Config();
	if (const auto object = ReadJson(u"ayu_fancy.json"_q)) {
		const auto enabled = object->value(u"enabled"_q);
		result.enabled = enabled.isBool() ? enabled.toBool() : true;
		const auto userConfig = object->value(u"codex_user_config"_q);
		result.codexUserConfig = userConfig.isBool() && userConfig.toBool();
		result.engine = ReadString(*object, "engine", result.engine).toLower();
		result.codexPath = ReadString(*object, "codex_path", result.codexPath);
		result.codexModel = ReadString(*object, "codex_model", result.codexModel);
		result.codexEffort = ReadString(
			*object,
			"codex_effort",
			result.codexEffort);
		result.key = ReadString(*object, "key", result.key);
		result.baseUrl = ReadString(*object, "base_url", result.baseUrl);
		result.model = ReadString(*object, "model", result.model);
		result.style = ReadString(*object, "style", result.style);
	}
	if (result.key.isEmpty()) {
		if (const auto voice = ReadJson(u"ayu_voice.json"_q)) {
			result.key = ReadString(*voice, "key", QString());
		}
	}
	if (result.key.isEmpty()) {
		const auto env = QProcessEnvironment::systemEnvironment();
		result.key = env.value(u"AYU_FANCY_KEY"_q, env.value(u"GROQ_API_KEY"_q));
	}
	while (result.baseUrl.endsWith('/')) {
		result.baseUrl.chop(1);
	}
	return result;
}

// npm puts a codex.cmd shim on PATH, the real codex.exe is in node_modules.
[[nodiscard]] QString NpmCodexExe(const QString &shimDir) {
	const auto triples = {
		std::pair{ u"codex-win32-x64"_q, u"x86_64-pc-windows-msvc"_q },
		std::pair{ u"codex-win32-arm64"_q, u"aarch64-pc-windows-msvc"_q },
	};
	const auto roots = {
		shimDir + u"/node_modules/@openai/codex/node_modules/@openai/"_q,
		shimDir + u"/node_modules/@openai/"_q,
	};
	for (const auto &root : roots) {
		for (const auto &[package, triple] : triples) {
			for (const auto &tail : { u"/bin/codex.exe"_q, u"/codex/codex.exe"_q }) {
				const auto path = root + package + u"/vendor/"_q + triple + tail;
				if (QFileInfo::exists(path)) {
					return QDir::toNativeSeparators(path);
				}
			}
		}
	}
	return QString();
}

[[nodiscard]] QString FindCodex(const Config &config) {
	if (!config.codexPath.isEmpty()) {
		return QFileInfo::exists(config.codexPath)
			? config.codexPath
			: QString();
	}
	auto candidates = QStringList();
	candidates.push_back(QStandardPaths::findExecutable(u"codex"_q));
#ifdef Q_OS_WIN
	const auto env = QProcessEnvironment::systemEnvironment();
	candidates.push_back(QStandardPaths::findExecutable(u"codex.cmd"_q));
	candidates.push_back(env.value(u"APPDATA"_q) + u"/npm/codex.cmd"_q);
#endif // Q_OS_WIN
	for (const auto &candidate : candidates) {
		if (candidate.isEmpty() || !QFileInfo::exists(candidate)) {
			continue;
		}
		const auto info = QFileInfo(candidate);
		const auto suffix = info.suffix().toLower();
		if (suffix == u"cmd"_q || suffix == u"bat"_q || suffix == u"ps1"_q) {
			const auto exe = NpmCodexExe(info.absolutePath());
			if (!exe.isEmpty()) {
				return exe;
			} else if (suffix == u"ps1"_q) {
				continue;
			}
		}
		return QDir::toNativeSeparators(info.absoluteFilePath());
	}
	return QString();
}

[[nodiscard]] QString FirstLine(const QByteArray &data) {
	const auto lines = QString::fromUtf8(data).split('\n', Qt::SkipEmptyParts);
	for (auto i = lines.size(); i != 0; --i) {
		const auto line = lines[i - 1].trimmed();
		if (line.contains(u"error"_q, Qt::CaseInsensitive)) {
			return line.left(200);
		}
	}
	return lines.isEmpty() ? QString() : lines.back().trimmed().left(200);
}

[[nodiscard]] QString LoginError() {
	return u"Codex не вошёл в аккаунт, запусти AYU_FANCY.cmd"_q;
}

void AskCodex(
		const Config &config,
		const QString &codex,
		const QString &prompt,
		AnswerCallback done) {
	auto temp = std::make_shared<QTemporaryDir>();
	if (!temp->isValid()) {
		done({ .error = u"нет временной папки"_q, .engine = u"Codex"_q });
		return;
	}
	const auto output = temp->filePath(u"answer.txt"_q);
	auto args = QStringList{
		u"exec"_q,
		u"--skip-git-repo-check"_q,
		u"--ephemeral"_q,
		u"--ignore-rules"_q,
		u"--sandbox"_q,
		u"read-only"_q,
		u"--color"_q,
		u"never"_q,
		u"-C"_q,
		QDir::toNativeSeparators(temp->path()),
		u"-o"_q,
		QDir::toNativeSeparators(output),
	};
	if (!config.codexUserConfig) {
		// MCP servers and plugins from the user config only slow it down.
		args.push_back(u"--ignore-user-config"_q);
	}
	if (!config.codexEffort.isEmpty()) {
		args.push_back(u"-c"_q);
		args.push_back(u"model_reasoning_effort="_q + config.codexEffort);
	}
	if (!config.codexModel.isEmpty()) {
		args.push_back(u"-m"_q);
		args.push_back(config.codexModel);
	}
	args.push_back(u"-"_q);

	const auto process = new QProcess();
	const auto suffix = QFileInfo(codex).suffix().toLower();
	if (suffix == u"cmd"_q || suffix == u"bat"_q) {
#ifdef Q_OS_WIN
		auto line = u"\"\""_q + codex + '"';
		for (const auto &arg : args) {
			line += u" \""_q + arg + '"';
		}
		process->setProgram(u"cmd.exe"_q);
		process->setNativeArguments(u"/d /s /c "_q + line + '"');
#endif // Q_OS_WIN
	} else {
		process->setProgram(codex);
		process->setArguments(args);
	}
#ifdef Q_OS_WIN
	process->setCreateProcessArgumentsModifier([](
			QProcess::CreateProcessArguments *arguments) {
		arguments->flags |= kCreateNoWindow;
	});
#endif // Q_OS_WIN
	process->setWorkingDirectory(temp->path());

	const auto finished = std::make_shared<bool>(false);
	const auto finish = [=](Answer answer) {
		if (*finished) {
			return;
		}
		*finished = true;
		answer.engine = u"Codex"_q;
		process->deleteLater();
		done(std::move(answer));
	};
	const auto timer = new QTimer(process);
	timer->setSingleShot(true);
	QObject::connect(timer, &QTimer::timeout, [=] {
		LOG(("AyuFancy: codex timeout"));
		process->kill();
		finish({ .error = u"Codex не ответил за 2 минуты"_q });
	});
	// Without login or network Codex keeps reconnecting for minutes.
	const auto errors = std::make_shared<QByteArray>();
	QObject::connect(process, &QProcess::readyReadStandardError, [=] {
		errors->append(process->readAllStandardError());
		const auto text = QString::fromUtf8(*errors);
		const auto has = [&](const char *what) {
			return text.contains(QLatin1String(what), Qt::CaseInsensitive);
		};
		static const auto status401 = QRegularExpression(
			u"(?<![\\d.:-])401(?!\\d)"_q);
		if (text.contains(status401)
			|| has("unauthorized")
			|| has("not logged in")) {
			LOG(("AyuFancy: codex is not logged in"));
			process->kill();
			finish({ .error = LoginError() });
		} else if (has("waiting for network")) {
			LOG(("AyuFancy: codex has no network"));
			process->kill();
			finish({ .error = u"Codex: нет связи с OpenAI"_q });
		}
	});
	QObject::connect(process, &QProcess::errorOccurred, [=](
			QProcess::ProcessError error) {
		if (error == QProcess::FailedToStart) {
			LOG(("AyuFancy: codex failed to start: %1").arg(codex));
			finish({ .error = u"Codex не запустился"_q });
		}
	});
	QObject::connect(process, &QProcess::finished, [=](
			int code,
			QProcess::ExitStatus status) {
		errors->append(process->readAllStandardError());
		auto file = QFile(output);
		const auto answer = file.open(QIODevice::ReadOnly)
			? QString::fromUtf8(file.readAll()).trimmed()
			: QString();
		temp->remove();
		if (status == QProcess::NormalExit && !code && !answer.isEmpty()) {
			finish({ .text = answer });
			return;
		}
		const auto line = FirstLine(*errors);
		LOG(("AyuFancy: codex exit %1, %2").arg(code).arg(line));
		const auto login = line.contains(u"login"_q, Qt::CaseInsensitive)
			|| line.contains(u"auth"_q, Qt::CaseInsensitive)
			|| line.contains(u"401"_q);
		finish({ .error = login
			? LoginError()
			: (status == QProcess::NormalExit && !code)
			? u"Codex вернул пустой ответ"_q
			: (u"Codex: "_q + (line.isEmpty()
				? (u"код "_q + QString::number(code))
				: line)) });
	});
	timer->start(kCodexTimeoutMs);
	process->start();
	process->write(prompt.toUtf8());
	process->closeWriteChannel();
}

[[nodiscard]] QNetworkAccessManager *Manager() {
	static const auto result = new QNetworkAccessManager(
		QCoreApplication::instance());
	return result;
}

void AskChat(
		const Config &config,
		const QString &model,
		const QString &prompt,
		AnswerCallback done) {
	auto request = QNetworkRequest(
		QUrl(config.baseUrl + u"/chat/completions"_q));
	request.setHeader(
		QNetworkRequest::ContentTypeHeader,
		u"application/json"_q);
	request.setRawHeader("Authorization", "Bearer " + config.key.toUtf8());
	request.setTransferTimeout(kHttpTimeoutMs);

	auto body = QJsonObject{
		{ u"model"_q, model },
		{ u"temperature"_q, 0.4 },
		{ u"messages"_q, QJsonArray{ QJsonObject{
			{ u"role"_q, u"user"_q },
			{ u"content"_q, prompt },
		} } },
	};
	if (model.contains(u"gpt-oss"_q)) {
		body.insert(u"reasoning_effort"_q, u"low"_q);
	}
	const auto reply = Manager()->post(
		request,
		QJsonDocument(body).toJson(QJsonDocument::Compact));
	QObject::connect(reply, &QNetworkReply::finished, [=] {
		reply->deleteLater();
		const auto status = reply->attribute(
			QNetworkRequest::HttpStatusCodeAttribute).toInt();
		const auto data = reply->readAll();
		const auto object = QJsonDocument::fromJson(data).object();
		const auto engine = u"Groq"_q;
		if (status == 200) {
			const auto text = object.value(u"choices"_q).toArray().at(0)
				.toObject().value(u"message"_q).toObject()
				.value(u"content"_q).toString().trimmed();
			if (!text.isEmpty()) {
				done({ .text = text, .engine = engine });
				return;
			}
		}
		const auto message = object.value(u"error"_q).toObject()
			.value(u"message"_q).toString();
		LOG(("AyuFancy: chat %1 %2: %3"
			).arg(model).arg(status).arg(message.left(200)));
		const auto modelGone = (status == 404 || status == 400)
			&& message.contains(u"model"_q, Qt::CaseInsensitive);
		if (modelGone && model != QLatin1String(kFallbackModel)) {
			AskChat(config, QLatin1String(kFallbackModel), prompt, done);
			return;
		}
		done({
			.error = (status == 401)
				? u"Groq: ключ не подходит"_q
				: status
				? (u"Groq: ошибка "_q + QString::number(status))
				: u"Groq: нет сети"_q,
			.engine = engine,
		});
	});
}

// Codex first, Groq when Codex is missing or failed (engine "auto").
void Ask(const Config &config, const QString &prompt, AnswerCallback done) {
	const auto codex = (config.engine != u"groq"_q)
		? FindCodex(config)
		: QString();
	const auto chat = (config.engine != u"codex"_q) && !config.key.isEmpty();
	if (codex.isEmpty() && !chat) {
		done({ .error = (config.engine == u"codex"_q)
			? u"Codex не найден, запусти AYU_FANCY.cmd"_q
			: u"Нет ни Codex, ни ключа Groq, запусти AYU_FANCY.cmd"_q });
		return;
	}
	const auto viaChat = [=](QString previous) {
		AskChat(config, config.model, prompt, [=](Answer answer) {
			if (!answer.error.isEmpty() && !previous.isEmpty()) {
				answer.error = previous + u"; "_q + answer.error;
			}
			done(std::move(answer));
		});
	};
	if (codex.isEmpty()) {
		viaChat(QString());
		return;
	}
	LOG(("AyuFancy: codex %1").arg(codex));
	AskCodex(config, codex, prompt, [=](Answer answer) {
		if (answer.error.isEmpty() || !chat) {
			done(std::move(answer));
		} else {
			viaChat(answer.error);
		}
	});
}

void Run(
		std::shared_ptr<Main::SessionShow> show,
		QPointer<Ui::InputField> field) {
	const auto toast = [=](const QString &text) {
		if (show->valid()) {
			show->showToast(text);
		}
	};
	const auto raw = field.data();
	if (!raw || Busy.contains(raw)) {
		return;
	}
	const auto config = LoadConfig();
	if (!config.enabled) {
		return;
	}
	const auto full = raw->getTextWithTags();
	if (full.text.trimmed().isEmpty()) {
		toast(u"✨ Сначала напиши текст"_q);
		return;
	}

	// Only the selection, when there is one.
	auto from = 0;
	auto till = int(full.text.size());
	auto part = full;
	const auto cursor = raw->textCursor();
	if (cursor.hasSelection()) {
		const auto start = cursor.selectionStart();
		const auto end = cursor.selectionEnd();
		const auto before = raw->getTextWithTagsPart(0, start);
		const auto selected = raw->getTextWithTagsPart(start, end);
		if (!selected.text.trimmed().isEmpty()
			&& full.text.mid(before.text.size(), selected.text.size())
				== selected.text) {
			from = before.text.size();
			till = from + selected.text.size();
			part = selected;
		}
	}

	const auto prepared = Protect(part);
	const auto prompt = BuildPrompt(ToHtml(prepared.text), config.style);
	Busy.emplace(raw);
	const auto waiting = std::make_shared<bool>(true);
	Ask(config, prompt, [=](Answer answer) {
		*waiting = false;
		Busy.erase(raw);
		const auto strong = field.data();
		if (!strong) {
			return;
		} else if (!answer.error.isEmpty()) {
			toast(u"✨ "_q + answer.error);
			return;
		} else if (strong->getTextWithTags() != full) {
			toast(u"✨ Текст поменялся, пока ждал ответ. Жми ещё раз"_q);
			return;
		}
		const auto restored = Restore(
			FromHtml(CleanAnswer(answer.text)),
			prepared.items);
		if (!LooksSane(part, restored.text)) {
			LOG(("AyuFancy: refused answer, %1 -> %2 chars"
				).arg(part.text.size()).arg(restored.text.text.size()));
			toast(u"✨ %1 вернул что-то не то, текст не тронут"_q
				.arg(answer.engine));
			return;
		}
		strong->setTextWithTags(
			Splice(full, from, till, restored.text),
			Ui::InputField::HistoryAction::NewEntry);
		const auto edit = strong->rawTextEdit();
		auto end = edit->textCursor();
		end.movePosition(QTextCursor::End);
		edit->setTextCursor(end);
		strong->setFocus();
		toast(restored.lost
			? u"✨ Готово (%1), %2 эмодзи не вернулись. Ctrl+Z вернёт как было"_q
				.arg(answer.engine)
				.arg(restored.lost)
			: u"✨ Готово (%1). Ctrl+Z вернёт как было"_q.arg(answer.engine));
	});
	if (*waiting) {
		toast(u"✨ Оформляю…"_q);
	}
}

[[nodiscard]] bool IsFancyShortcut(not_null<QKeyEvent*> e) {
	const auto modifiers = e->modifiers()
		& (Qt::ControlModifier
			| Qt::ShiftModifier
			| Qt::AltModifier
			| Qt::MetaModifier);
	if (modifiers != (Qt::ControlModifier | Qt::ShiftModifier)) {
		return false;
	}
#ifdef Q_OS_WIN
	// Russian layout gives a Cyrillic key, the virtual key is still F.
	if (e->nativeVirtualKey() == 0x46) {
		return true;
	}
#endif // Q_OS_WIN
	return (e->key() == Qt::Key_F);
}

class ShortcutFilter final : public QObject {
public:
	ShortcutFilter(QObject *parent, Fn<void()> callback)
	: QObject(parent)
	, _callback(std::move(callback)) {
	}

protected:
	bool eventFilter(QObject *watched, QEvent *e) override {
		const auto type = e->type();
		if (type == QEvent::ShortcutOverride || type == QEvent::KeyPress) {
			const auto key = static_cast<QKeyEvent*>(e);
			if (IsFancyShortcut(key)) {
				e->accept();
				if (type == QEvent::KeyPress && !key->isAutoRepeat()) {
					_callback();
				}
				return true;
			}
		}
		return QObject::eventFilter(watched, e);
	}

private:
	Fn<void()> _callback;

};

} // namespace

void SetupField(
		std::shared_ptr<Main::SessionShow> show,
		not_null<Ui::InputField*> field) {
	const auto weak = QPointer<Ui::InputField>(field.get());
	const auto run = [=] {
		Run(show, weak);
	};
	const auto edit = field->rawTextEdit();
	edit->installEventFilter(new ShortcutFilter(edit, run));

	field->addContextMenuHook([=](Ui::InputField::ContextMenuRequest request) {
		const auto strong = weak.data();
		if (!strong || !LoadConfig().enabled) {
			return;
		}
		const auto menu = request.menu;
		const auto action = new QAction(
			u"✨ Красиво (Codex)\t"_q
				+ QKeySequence(Qt::CTRL | Qt::SHIFT | Qt::Key_F).toString(
					QKeySequence::NativeText),
			menu);
		action->setDisabled(strong->getLastText().trimmed().isEmpty()
			|| Busy.contains(strong));
		QObject::connect(action, &QAction::triggered, run);
		const auto first = menu->actions().value(0);
		menu->insertAction(first, action);
		if (first) {
			menu->insertSeparator(first);
		}
	});
}

} // namespace AyuFeatures::FancyFormat
