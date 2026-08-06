package com.hyatin.agentbell.localization

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

class AndroidStringResourceTest {
    @Test fun englishAndSimplifiedChineseResourcesHaveIdenticalNonEmptyKeysAndFormats() {
        val root = findAppRoot()
        val englishFile = File(root, "src/main/res/values/strings.xml")
        val chineseFile = File(root, "src/main/res/values-zh-rCN/strings.xml")
        val english = readStrings(englishFile)
        val chinese = readStrings(chineseFile)

        assertEquals(english.keys, chinese.keys)
        english.keys.forEach { key ->
            assertTrue(english.getValue(key).isNotBlank())
            assertTrue(chinese.getValue(key).isNotBlank())
            assertEquals(
                placeholders(english.getValue(key)),
                placeholders(chinese.getValue(key)),
            )
        }
        listOf(
            "pairing_scanner_content_description",
            "notification_connection_channel",
            "notification_connection_channel_description",
            "notification_completed_channel",
            "notification_completed_channel_description",
            "notification_connection_connected",
        ).forEach { required -> assertTrue(required in english) }

        val englishPlurals = readPlurals(englishFile)
        val chinesePlurals = readPlurals(chineseFile)
        assertEquals(englishPlurals.keys, chinesePlurals.keys)
        englishPlurals.forEach { (key, quantities) ->
            assertEquals(quantities.keys, chinesePlurals.getValue(key).keys)
            quantities.forEach { (quantity, value) ->
                val translated = chinesePlurals.getValue(key).getValue(quantity)
                assertTrue(value.isNotBlank())
                assertTrue(translated.isNotBlank())
                assertEquals(placeholders(value), placeholders(translated))
            }
        }
    }

    @Test fun localeConfigDeclaresOnlyEnglishAndSimplifiedChinese() {
        val file = File(findAppRoot(), "src/main/res/xml/locales_config.xml")
        val text = file.readText()
        assertTrue(text.contains("android:name=\"en\""))
        assertTrue(text.contains("android:name=\"zh-CN\""))
    }

    private fun readStrings(file: File): Map<String, String> {
        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file)
        val nodes = document.getElementsByTagName("string")
        val values = linkedMapOf<String, String>()
        repeat(nodes.length) { index ->
            val element = nodes.item(index)
            val name = element.attributes.getNamedItem("name").nodeValue
            check(name !in values) { "Duplicate string resource: $name" }
            values[name] = element.textContent
        }
        return values
    }

    private fun readPlurals(file: File): Map<String, Map<String, String>> {
        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file)
        val nodes = document.getElementsByTagName("plurals")
        val values = linkedMapOf<String, Map<String, String>>()
        repeat(nodes.length) { index ->
            val element = nodes.item(index)
            val name = element.attributes.getNamedItem("name").nodeValue
            check(name !in values) { "Duplicate plurals resource: $name" }
            val items = linkedMapOf<String, String>()
            val children = element.childNodes
            repeat(children.length) { childIndex ->
                val child = children.item(childIndex)
                if (child.nodeName == "item") {
                    val quantity = child.attributes.getNamedItem("quantity").nodeValue
                    check(quantity !in items) { "Duplicate plural quantity: $name/$quantity" }
                    items[quantity] = child.textContent
                }
            }
            values[name] = items
        }
        return values
    }

    private fun placeholders(value: String): List<String> =
        Regex("%\\d+\\$[sd]").findAll(value).map { it.value }.toList()

    private fun findAppRoot(): File {
        var current: File? = File(System.getProperty("user.dir") ?: error("user.dir missing"))
        while (current != null) {
            if (File(current, "src/main/res/values/strings.xml").isFile) return current
            val nested = File(current, "app/src/main/res/values/strings.xml")
            if (nested.isFile) return File(current, "app")
            current = current.parentFile
        }
        error("Android app module root was not found")
    }
}
