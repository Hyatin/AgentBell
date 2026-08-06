package com.hyatin.agentbell.localization

import org.junit.Assert.assertEquals
import org.junit.Test

class AppLanguageTest {
    @Test fun persistedValuesAreStrictAndInvalidFallsBackToSystem() {
        assertEquals(AppLanguage.SYSTEM, AppLanguage.fromPersistedValue(null))
        assertEquals(AppLanguage.SYSTEM, AppLanguage.fromPersistedValue("unsupported"))
        assertEquals(AppLanguage.ENGLISH, AppLanguage.fromPersistedValue("en-US"))
        assertEquals(AppLanguage.CHINESE_SIMPLIFIED, AppLanguage.fromPersistedValue("zh-CN"))
    }

    @Test fun systemModeUsesOnlyExactSimplifiedChinese() {
        assertEquals(
            AppLanguage.CHINESE_SIMPLIFIED,
            AppLanguage.effectiveLanguage(AppLanguage.SYSTEM, "zh-CN"),
        )
        listOf("en-US", "zh-TW", "zh-HK", "ja-JP", "de-DE").forEach { tag ->
            assertEquals(
                AppLanguage.ENGLISH,
                AppLanguage.effectiveLanguage(AppLanguage.SYSTEM, tag),
            )
        }
    }

    @Test fun manualSelectionOverridesSystemLanguage() {
        assertEquals(
            AppLanguage.CHINESE_SIMPLIFIED,
            AppLanguage.effectiveLanguage(AppLanguage.CHINESE_SIMPLIFIED, "en-US"),
        )
        assertEquals(
            AppLanguage.ENGLISH,
            AppLanguage.effectiveLanguage(AppLanguage.ENGLISH, "zh-CN"),
        )
    }
}
