package com.hyatin.agentbell.localization

import androidx.appcompat.app.AppCompatDelegate
import androidx.core.os.LocaleListCompat

enum class AppLanguage(val persistedValue: String) {
    SYSTEM("system"),
    ENGLISH("en-US"),
    CHINESE_SIMPLIFIED("zh-CN"),
    ;

    companion object {
        fun fromPersistedValue(value: String?): AppLanguage = when (value) {
            ENGLISH.persistedValue -> ENGLISH
            CHINESE_SIMPLIFIED.persistedValue -> CHINESE_SIMPLIFIED
            else -> SYSTEM
        }

        fun effectiveLanguage(preference: AppLanguage, systemLanguageTag: String): AppLanguage =
            when (preference) {
                ENGLISH -> ENGLISH
                CHINESE_SIMPLIFIED -> CHINESE_SIMPLIFIED
                SYSTEM -> if (systemLanguageTag.equals("zh-CN", ignoreCase = true)) {
                    CHINESE_SIMPLIFIED
                } else {
                    ENGLISH
                }
            }
    }
}

object AppLanguageController {
    fun current(): AppLanguage {
        val locales = AppCompatDelegate.getApplicationLocales()
        if (locales.isEmpty) return AppLanguage.SYSTEM
        val locale = locales[0] ?: return AppLanguage.SYSTEM
        return if (locale.language.equals("zh", ignoreCase = true) &&
            locale.country.equals("CN", ignoreCase = true)
        ) {
            AppLanguage.CHINESE_SIMPLIFIED
        } else {
            AppLanguage.ENGLISH
        }
    }

    fun set(language: AppLanguage) {
        val locales = when (language) {
            AppLanguage.SYSTEM -> LocaleListCompat.getEmptyLocaleList()
            AppLanguage.ENGLISH -> LocaleListCompat.forLanguageTags("en")
            AppLanguage.CHINESE_SIMPLIFIED -> LocaleListCompat.forLanguageTags("zh-CN")
        }
        AppCompatDelegate.setApplicationLocales(locales)
    }
}
