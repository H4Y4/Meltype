// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

using Meltype.Config;

namespace Meltype.Tests;

internal static class SettingsJsonTests
{
    [Test]
    public static void SettingsJson_PreservesEnumsAndSpacingAcrossSaveLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            var original = new Settings
            {
                SpaceAroundEnglish = true, InputStyle = InputStyle.Kana,
                DetectionLevel = DetectionLevel.Manual, Mode = InputMode.AutoSwitch,
            }.Normalize();
            original.Save(path);
            Assert.True(original.ToJson().Contains("\"AutoSwitch\""), "enum の文字列表現を維持する");
            var loaded = Settings.Load(path);
            Assert.True(loaded.SpaceAroundEnglish, "空白設定を読み込む");
            Assert.Equal(InputStyle.Kana, loaded.InputStyle);
            Assert.Equal(DetectionLevel.Manual, loaded.DetectionLevel);
            Assert.Equal(InputMode.AutoSwitch, loaded.Mode);
            Assert.True(!File.Exists(path + ".broken"), "有効な設定を壊れたファイルとして扱わない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_StillAcceptsCommentsTrailingCommasAndNumericEnums()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ /* synthetic */ \"SettingsVersion\": 5, \"SpaceAroundEnglish\": true, \"InputStyle\": 1, }");
            var loaded = Settings.Load(path);
            Assert.True(loaded.SpaceAroundEnglish, "コメント・末尾カンマを受け入れる");
            Assert.Equal(InputStyle.Kana, loaded.InputStyle, "旧形式の数値 enum を受け入れる");
            Assert.True(!File.Exists(path + ".broken"), "有効な旧形式を壊れたファイルとして扱わない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_RoundTripsByName()
    {
        Assert.Equal(ControlKeyStyle.Atok, new Settings().ControlKeys, "既定は ATOK 式");
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            new Settings { ControlKeys = ControlKeyStyle.Mac }.Normalize().Save(path);
            Assert.True(File.ReadAllText(path).Contains("\"ControlKeys\": \"Mac\""), "名前で保存する");
            Assert.Equal(ControlKeyStyle.Mac, Settings.Load(path).ControlKeys);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_MissingIsAtokAndCaseInsensitive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"SpaceAroundEnglish\": true }");
            Assert.Equal(ControlKeyStyle.Atok, Settings.Load(path).ControlKeys, "書いていなければ ATOK 式");
            // ほかの列挙型 (Punctuation) と同じく、名前の大文字小文字は問わず、数値でも書ける
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": \"mac\", \"Punctuation\": \"fullwidthcommaperiod\" }");
            var loaded = Settings.Load(path);
            Assert.Equal(ControlKeyStyle.Mac, loaded.ControlKeys, "大文字小文字は問わない");
            Assert.Equal(PunctuationStyle.FullWidthCommaPeriod, loaded.Punctuation, "Punctuation も同じ");
            File.WriteAllText(path, "{\"SettingsVersion\": 5, \"ControlKeys\": \"Mac\"}");
            var minimal = Settings.Load(path);
            Assert.Equal(ControlKeyStyle.Mac, minimal.ControlKeys, "ControlKeys だけの config.json");
            Assert.Equal(new Settings().Punctuation, minimal.Punctuation, "ほかの設定は既定値");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": 1 }");
            Assert.Equal(ControlKeyStyle.Mac, Settings.Load(path).ControlKeys, "数値でも読める (ほかの列挙型と同じ)");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_UnknownValueBehavesLikeOtherEnums()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            // 知らない名前・null・配列・空文字は、ほかの列挙型 (Punctuation) と同じく config.json 全体が既定値に戻り、.broken に退避される
            foreach (var key in new[] { "ControlKeys", "Punctuation" })
                foreach (var value in new[] { "\"Emacs\"", "null", "[1]", "\"\"" })
                {
                    File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"" + key + "\": " + value + ", \"SpaceAroundEnglish\": true }");
                    var loaded = Settings.Load(path);
                    Assert.Equal(ControlKeyStyle.Atok, loaded.ControlKeys, key + "=" + value);
                    Assert.True(!loaded.SpaceAroundEnglish, "設定全体が既定値に戻る: " + key + "=" + value);
                    Assert.True(File.Exists(path + ".broken"), ".broken に退避する: " + key + "=" + value);
                    File.Delete(path + ".broken");
                }
            // 定義の無い数値は、ほかの列挙型と同じくそのまま読む。入力中の Ctrl キーでは Mac 以外 = ATOK 式として動く
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": 99, \"SpaceAroundEnglish\": true }");
            var undefined = Settings.Load(path);
            Assert.True(undefined.ControlKeys != ControlKeyStyle.Mac, "定義の無い数値は Mac にならない");
            Assert.True(undefined.SpaceAroundEnglish, "定義の無い数値では設定全体は戻らない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
