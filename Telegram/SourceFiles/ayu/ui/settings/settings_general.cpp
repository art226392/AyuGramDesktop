// This is the source code of AyuGram for Desktop.
//
// We do not and cannot prevent the use of our code,
// but be respectful and credit the original author.
//
// Copyright @Radolyn, 2026
#include "ayu/ui/settings/settings_general.h"

#include "lang_auto.h"
#include "ayu/ayu_settings.h"
#include "ayu/features/dictation/ayu_dictation.h"
#include "ayu/ui/boxes/edit_mark_box.h"
#include "ayu/ui/settings/ayu_builder.h"
#include "ayu/ui/settings/settings_ayu_utils.h"
#include "ayu/ui/settings/settings_main.h"
#include "base/platform/base_platform_info.h"
#include "core/application.h"
#include "lang/lang_text_entity.h"
#include "platform/platform_translate_provider.h"
#include "settings/settings_builder.h"
#include "settings/settings_common.h"
#include "styles/style_menu_icons.h"
#include "styles/style_settings.h"
#include "ui/boxes/single_choice_box.h"
#include "ui/toast/toast.h"
#include "ui/widgets/buttons.h"
#include "ui/wrap/vertical_layout.h"
#include "window/window_controller.h"
#include "window/window_session_controller.h"

namespace Settings {

using namespace Builder;
using namespace AyuBuilder;

namespace {

void BuildTranslator(SectionBuilder &builder, AyuSectionBuilder &ayu) {
	builder.addSubsectionTitle(tr::lng_translate_settings_subtitle());

	auto *settings = &AyuSettings::getInstance();

	const auto options = std::vector{
		std::pair(TranslationProvider::Telegram, QString("Telegram")),
		std::pair(TranslationProvider::Google, QString("Google")),
		std::pair(TranslationProvider::Yandex, QString("Yandex")),
	};
	const auto nativeAvailable = Platform::IsTranslateProviderAvailable();
	auto availableOptions = options;
	if (nativeAvailable) {
		availableOptions.push_back(std::pair(
			TranslationProvider::Native,
			[] {
				if constexpr (Platform::IsMac()) {
					return QString("macOS");
				} else if constexpr (Platform::IsWindows()) {
					return QString("Windows");
				} else {
					return QString("Linux");
				}
			}()));
	}
	auto optionLabels = std::vector<QString>();
	optionLabels.reserve(availableOptions.size());
	for (const auto &option : availableOptions) {
		optionLabels.push_back(option.second);
	}

	const auto getIndex = [=](TranslationProvider val) {
		const auto i = ranges::find(
			availableOptions,
			val,
			&std::pair<TranslationProvider, QString>::first);
		return (i != end(availableOptions))
			? int(i - begin(availableOptions))
			: 0;
	};

	auto currentVal = AyuSettings::getInstance().translationProviderValue()
		| rpl::map(getIndex)
		| rpl::map([=](int val) { return availableOptions[val].second; });

	const auto button = builder.addButton({
		.id = u"ayu/translationProvider"_q,
		.title = tr::ayu_TranslationProvider(),
		.st = &st::settingsButtonNoIcon,
		.label = std::move(currentVal),
		.onClick = [=] {
			if (const auto controller = Core::App().activeWindow()->sessionController()) {
				controller->show(Box(
						[=](not_null<Ui::GenericBox*> box) {
							const auto save = [=](int index) {
								const auto option = availableOptions[index].first;
								AyuSettings::getInstance().setTranslationProvider(option);

								if constexpr (Platform::IsMac()) {
									if (option == TranslationProvider::Native) {
										controller->showToast(Ui::Toast::Config{
											.text = tr::lng_translate_settings_use_platform_mac_about(tr::now, tr::rich),
											.duration = 6 * crl::time(1000)
										});
									}
								}
							};
							SingleChoiceBox(box, {
								.title = tr::ayu_TranslationProvider(),
								.options = optionLabels,
								.initialSelection = getIndex(settings->translationProvider()),
								.callback = save,
							});
						}));
			}
		},
	});
	if (button) {
		ayu.addBetaBadge(button);
	}
}

void BuildDictation(SectionBuilder &builder, AyuSectionBuilder &ayu) {
	builder.addSubsectionTitle(tr::ayu_DictationSubtitle());

	auto *settings = &AyuSettings::getInstance();

	builder.addButton({
		.id = u"ayu/aiDictationKey"_q,
		.title = tr::ayu_DictationKey(),
		.st = &st::settingsButtonNoIcon,
		.label = settings->aiDictationKeyValue(
		) | rpl::map([](const QString &key) {
			return !key.trimmed().isEmpty()
				? tr::ayu_DictationKeySet(tr::now)
				: Ayu::Dictation::hasBuiltInKey()
				? tr::ayu_DictationKeyBuiltIn(tr::now)
				: tr::ayu_DictationKeyNotSet(tr::now);
		}),
		.onClick = [=] {
			const auto window = Core::App().activeWindow();
			const auto controller = window
				? window->sessionController()
				: nullptr;
			if (!controller) {
				return;
			}
			controller->show(Box<EditMarkBox>(
				tr::ayu_DictationKey(),
				settings->aiDictationKey(),
				QString(),
				[=](const QString &value) {
					AyuSettings::getInstance().setAiDictationKey(
						value.trimmed());
				}));
		},
		.keywords = { u"openai"_q, u"voice"_q, u"speech"_q },
	});

	ayu.addSettingToggle({
		.id = u"ayu/aiPolishOnEnter"_q,
		.title = tr::ayu_DictationPolishOnEnter(),
		.getter = &AyuSettings::aiPolishOnEnter,
		.setter = &AyuSettings::setAiPolishOnEnter,
	});

	const auto options = std::vector{
		std::pair(QString(), tr::ayu_DictationLanguageAuto(tr::now)),
		std::pair(u"ru"_q, QString::fromUtf8("Русский")),
		std::pair(u"en"_q, u"English"_q),
	};
	auto optionLabels = std::vector<QString>();
	for (const auto &option : options) {
		optionLabels.push_back(option.second);
	}
	const auto getIndex = [=](const QString &value) {
		const auto i = ranges::find(
			options,
			value,
			&std::pair<QString, QString>::first);
		return (i != end(options)) ? int(i - begin(options)) : 0;
	};
	builder.addButton({
		.id = u"ayu/aiPolishLanguage"_q,
		.title = tr::ayu_DictationLanguage(),
		.st = &st::settingsButtonNoIcon,
		.label = settings->aiPolishLanguageValue(
		) | rpl::map([=](const QString &value) {
			return options[getIndex(value)].second;
		}),
		.onClick = [=] {
			const auto window = Core::App().activeWindow();
			const auto controller = window
				? window->sessionController()
				: nullptr;
			if (!controller) {
				return;
			}
			controller->show(Box([=](not_null<Ui::GenericBox*> box) {
				SingleChoiceBox(box, {
					.title = tr::ayu_DictationLanguage(),
					.options = optionLabels,
					.initialSelection = getIndex(
						settings->aiPolishLanguage()),
					.callback = [=](int index) {
						AyuSettings::getInstance().setAiPolishLanguage(
							options[index].first);
					},
				});
			}));
		},
	});

	builder.addSkip();
	builder.addDividerText(tr::ayu_DictationAbout());
	builder.addSkip();
}

void BuildShowPeerId(SectionBuilder &builder) {
	auto *settings = &AyuSettings::getInstance();

	const auto options = std::vector{
		QString(tr::ayu_SettingsShowID_Hide(tr::now)),
		QString("Telegram API"),
		QString("Bot API")
	};

	auto currentVal = AyuSettings::getInstance().showPeerIdValue()
		| rpl::map([=](PeerIdDisplay val) {
			return options[static_cast<int>(val)];
		});

	const auto controller = builder.controller();
	builder.addButton({
		.id = u"ayu/showPeerId"_q,
		.altIds = { u"ayu/showIdAndDc"_q },
		.title = tr::ayu_SettingsShowID(),
		.st = &st::settingsButtonNoIcon,
		.label = std::move(currentVal),
		.onClick = [=] {
			controller->show(Box(
				[=](not_null<Ui::GenericBox*> box) {
					const auto save = [=](int index) {
						AyuSettings::getInstance().setShowPeerId(
							static_cast<PeerIdDisplay>(index));
					};
					SingleChoiceBox(box, {
						.title = tr::ayu_SettingsShowID(),
						.options = options,
						.initialSelection = static_cast<int>(settings->showPeerId()),
						.callback = save,
					});
				}));
		},
	});
}

void BuildQoLToggles(SectionBuilder &builder, AyuSectionBuilder &ayu) {
	auto *settings = &AyuSettings::getInstance();

	BuildTranslator(builder, ayu);
	ayu.addSectionDivider();

	BuildDictation(builder, ayu);

	builder.addSubsectionTitle(tr::ayu_CategoryGeneral());

	const auto controller = builder.controller();
	ayu.addToggle({
		.id = u"ayu/disableStories"_q,
		.altIds = { u"ayu/hideStories"_q },
		.title = tr::ayu_DisableStories(),
		.getter = [=] { return settings->disableStories(); },
		.setter = [=](bool enabled) {
			AyuSettings::getInstance().setDisableStories(enabled);
			ShowRestartPrompt(controller);
		},
	});

	ayu.addSettingToggle({
		.id = u"ayu/disableOpenLinkWarning"_q,
		.title = tr::ayu_DisableOpenLinkWarning(),
		.getter = &AyuSettings::disableOpenLinkWarning,
		.setter = &AyuSettings::setDisableOpenLinkWarning,
	});

	ayu.addCollapsibleToggle({
		.id = u"ayu/similarChannels"_q,
		.title = tr::ayu_DisableSimilarChannels(),
		.checkboxes = {
			NestedEntry{
				tr::ayu_CollapseSimilarChannels(tr::now),
				[] { return AyuSettings::getInstance().collapseSimilarChannels(); },
				[](bool v) { AyuSettings::getInstance().setCollapseSimilarChannels(v); }
			},
			NestedEntry{
				tr::ayu_HideSimilarChannelsTab(tr::now),
				[] { return AyuSettings::getInstance().hideSimilarChannels(); },
				[](bool v) { AyuSettings::getInstance().setHideSimilarChannels(v); }
			}
		},
		.toggledWhenAll = true,
	});

	ayu.addSettingToggle({
		.id = u"ayu/disableNotificationsDelay"_q,
		.title = tr::ayu_DisableNotificationsDelay(),
		.getter = &AyuSettings::disableNotificationsDelay,
		.setter = &AyuSettings::setDisableNotificationsDelay,
	});

	ayu.addSectionDivider();

	const auto zalgoButton = builder.addButton({
		.id = u"ayu/filterZalgo"_q,
		.title = tr::ayu_FilterZalgo(),
		.st = &st::settingsButtonNoIcon,
		.toggled = rpl::single(settings->filterZalgo()),
	});
	if (zalgoButton) {
		zalgoButton->toggledValue(
		) | rpl::filter(
			[=](bool enabled) {
				return (enabled != settings->filterZalgo());
			}
		) | on_next(
			[=](bool enabled) {
				AyuSettings::getInstance().setFilterZalgo(enabled);
				ShowRestartPrompt(controller);
			},
			zalgoButton->lifetime());
		ayu.addBetaBadge(zalgoButton);
	}

	ayu.addSettingToggle({
		.id = u"ayu/improveLinkPreviews"_q,
		.title = tr::ayu_ImproveLinkPreviews(),
		.getter = &AyuSettings::improveLinkPreviews,
		.setter = &AyuSettings::setImproveLinkPreviews,
	});
	ayu.addCollapsibleToggle({
		.id = u"ayu/confirmations"_q,
		.title = tr::ayu_ConfirmationsTitle(),
		.checkboxes = {
			NestedEntry{
				tr::ayu_StickerConfirmation(tr::now),
				[] { return AyuSettings::getInstance().stickerConfirmation(); },
				[](bool v) { AyuSettings::getInstance().setStickerConfirmation(v); }
			},
			NestedEntry{
				tr::ayu_GIFConfirmation(tr::now),
				[] { return AyuSettings::getInstance().gifConfirmation(); },
				[](bool v) { AyuSettings::getInstance().setGifConfirmation(v); }
			},
			NestedEntry{
				tr::ayu_VoiceConfirmation(tr::now),
				[] { return AyuSettings::getInstance().voiceConfirmation(); },
				[](bool v) { AyuSettings::getInstance().setVoiceConfirmation(v); }
			},
			NestedEntry{
				tr::ayu_RoundConfirmation(tr::now),
				[] { return AyuSettings::getInstance().roundConfirmation(); },
				[](bool v) { AyuSettings::getInstance().setRoundConfirmation(v); }
			}
		},
		.toggledWhenAll = false,
	});
	ayu.addSettingToggle({
		.id = u"ayu/showMessageSeconds"_q,
		.altIds = { u"ayu/formatTimeWithSeconds"_q },
		.title = tr::ayu_SettingsShowMessageSeconds(),
		.getter = &AyuSettings::showMessageSeconds,
		.setter = &AyuSettings::setShowMessageSeconds,
	});

	BuildShowPeerId(builder);

	ayu.addSectionDivider();

	builder.addSubsectionTitle(rpl::single(QString("Webview")));

	ayu.addSettingToggle({
		.id = u"ayu/spoofWebviewAsAndroid"_q,
		.title = tr::ayu_SettingsSpoofWebviewAsAndroid(),
		.getter = &AyuSettings::spoofWebviewAsAndroid,
		.setter = &AyuSettings::setSpoofWebviewAsAndroid,
	});

	ayu.addCollapsibleToggle({
		.id = u"ayu/biggerWindow"_q,
		.title = tr::ayu_SettingsBiggerWindow(),
		.checkboxes = {
			NestedEntry{
				tr::ayu_SettingsIncreaseWebviewHeight(tr::now),
				[] { return AyuSettings::getInstance().increaseWebviewHeight(); },
				[](bool v) { AyuSettings::getInstance().setIncreaseWebviewHeight(v); }
			},
			NestedEntry{
				tr::ayu_SettingsIncreaseWebviewWidth(tr::now),
				[] { return AyuSettings::getInstance().increaseWebviewWidth(); },
				[](bool v) { AyuSettings::getInstance().setIncreaseWebviewWidth(v); }
			}
		},
		.toggledWhenAll = false,
	});
}

const auto kMeta = BuildHelper({
	.id = AyuGeneral::Id(),
	.parentId = AyuMain::Id(),
	.title = &tr::ayu_CategoryGeneral,
	.icon = &st::menuIconShowAll,
}, [](SectionBuilder &builder) {
	auto ayu = AyuSectionBuilder(builder);

	builder.addSkip();
	BuildQoLToggles(builder, ayu);
	builder.addSkip();
});

} // namespace

rpl::producer<QString> AyuGeneral::title() {
	return tr::ayu_CategoryGeneral();
}

AyuGeneral::AyuGeneral(
	QWidget *parent,
	not_null<Window::SessionController*> controller)
: Section(parent, controller) {
	setupContent();
}

void AyuGeneral::setupContent() {
	const auto content = Ui::CreateChild<Ui::VerticalLayout>(this);
	build(content, kMeta.build);
	Ui::ResizeFitChild(this, content);
}

Type AyuGeneralId() {
	return AyuGeneral::Id();
}

} // namespace Settings
