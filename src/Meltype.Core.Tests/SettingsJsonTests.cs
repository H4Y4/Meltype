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
    public static void SettingsJson_ControlKeys_MissingOrUnknownIsAtok()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            foreach (var value in new[] { "", "\"ControlKeys\": \"Emacs\",", "\"ControlKeys\": 99,", "\"ControlKeys\": null,", "\"ControlKeys\": [1],", "\"ControlKeys\": \"\"," })
            {
                File.WriteAllText(path, "{ \"SettingsVersion\": 5, " + value + " \"SpaceAroundEnglish\": true }");
                var loaded = Settings.Load(path);
                Assert.Equal(ControlKeyStyle.Atok, loaded.ControlKeys, value);
                Assert.True(loaded.SpaceAroundEnglish, "知らない値でも、ほかの設定は読む: " + value);
                Assert.True(!File.Exists(path + ".broken"), "壊れたファイルとして扱わない: " + value);
            }
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": \"mac\" }");
            Assert.Equal(ControlKeyStyle.Mac, Settings.Load(path).ControlKeys, "大文字小文字は問わない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
