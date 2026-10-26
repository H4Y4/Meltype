// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>Mac 版・Linux 版から使う入力の本体 (MeltypeSession) のテスト。OS がキーを 1 つずつ渡し、使ったかをその場で返す。</summary>
internal static class SessionFacadeTests
{
    private static MeltypeSession Create() => new(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions(), () => new Settings());

    /// <summary>文字を 1 つずつ打つ (英字は大文字なら Shift 付き)。</summary>
    private static List<SessionResult> Type(MeltypeSession session, string text, string? before = null)
    {
        var results = new List<SessionResult>();
        foreach (var c in text)
        {
            var vk = c switch
            {
                ' ' => VirtualKeys.Space,
                '\n' => VirtualKeys.Return,
                '\b' => VirtualKeys.Back,
                ',' => VirtualKeys.OemComma,
                '.' => VirtualKeys.OemPeriod,
                '-' => VirtualKeys.OemMinus,
                '"' or '\'' => 0xDE,
                '#' => '3',
                '/' => VirtualKeys.Oem2,
                _ when char.IsAsciiLetter(c) => char.ToUpperInvariant(c),
                _ => c,
            };
            char? ch = c is ' ' or '\n' or '\b' ? null : c;
            results.Add(session.HandleKey(vk, ch, char.IsAsciiLetterUpper(c), false, false, false, before));
        }
        return results;
    }

    [Test]
    public static void MacMixedInputRegression()
    {
        foreach (var (input, expected) in new[] {
            ("wsldeshell", "wslでshell"),
            ("kyouhagoogledekensaku", "今日はgoogleで検索"),
            ("seeyouagain", "see you again"),
            ("I want to go to the park", "I want to go to the park"),
            ("Zorgblax", "Zorgblax"),
            ("stackoverflow", "stackoverflow"),
            ("https://example.com", "https://example.com"),
            ("alice@example.com", "alice@example.com"),
            ("getUserName", "getUserName"),
            ("google  shell", "google  shell"),
            ("google\u3000shell", "google\u3000shell"),
            ("get_user_name", "get_user_name") })
        {
            var session = new MeltypeSession(CompositionDetector.CreateDefault(), new CompositionTests.FakeConverter(),
                new CompositionOptions { LiveConversion = () => true, AutomaticEnglishSpacing = () => true }, () => new Settings());
            var results = Type(session, input + "\n");
            var output = "";
            for (var i = 0; i < results.Count; i++)
            {
                foreach (var edit in results[i].Commits)
                    output = output[..Math.Max(0, output.Length - edit.DeleteBefore)] + edit.Text;
                if (!results[i].Consumed) output += (input + "\n")[i];
            }
            Assert.Equal(expected, output, input);
        }
    }

    [Test]
    public static void Romaji_ComposesAndEnterCommits()
    {
        var session = Create();
        var results = Type(session, "kyouha");
        Assert.True(results.All(r => r.Consumed), "打った英字はアプリに渡さない");
        Assert.Equal("きょうは", results[^1].View?.Text);
        var enter = Type(session, "\n")[0];
        Assert.True(enter.Consumed, "Enter は確定に使う");
        Assert.Equal("きょうは", enter.Commits.Single().Text);
        Assert.True(enter.View is null, "確定したら変換ボックスを閉じる");
    }

    [Test]
    public static void CtrlEnterDuringCompositionCommitsWithoutSending()
    {
        // 変換中の Ctrl+Enter (送信のショートカット) は確定だけ行い、キーはアプリへ渡さない。
        // 渡すと、アプリが確定を反映する前に送信し、確定した文字が欠けた内容が送られる。
        var session = Create();
        Type(session, "tadokoro");
        var chord = session.HandleKey(VirtualKeys.Return, null, false, true, false, false);
        Assert.True(chord.Consumed, "変換中の Ctrl+Enter はアプリへ渡さない");
        Assert.Equal("たどころ", chord.Commits.Single().Text);
        Assert.True(chord.View is null, "確定したら変換ボックスを閉じる");

        // 変換が無い状態の Ctrl+Enter はアプリへ渡す (アプリが送信できる)。
        var again = session.HandleKey(VirtualKeys.Return, null, false, true, false, false);
        Assert.True(!again.Consumed, "確定後の Ctrl+Enter はアプリへ渡す");
        Assert.Equal(0, again.Commits.Count, "確定後の Ctrl+Enter は何も確定しない");
    }

    [Test]
    public static void EnglishWord_SpaceCommitsWithSpace()
    {
        var session = Create();
        var results = Type(session, "google ");
        Assert.Equal("google ", results[^1].Commits.Single().Text);
    }

    [Test]
    public static void TsfDirectMode_RomajiDetectionSwitchesToJapaneseWithoutReplayingLastKey()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { ClassifyDirect = (letters, _) => letters.Length >= 2 ? Detection.Verdict.Japanese : Detection.Verdict.Undecided },
            () => settings, directModeDetection: true)
        {
            Direct = true,
        };

        var results = Type(session, "ka");

        Assert.True(!results[0].Consumed, "判定中の1文字目は英数のまま入力欄へ渡す");
        Assert.True(results[1].Consumed, "日本語と判定したキーはローマ字として再送しない");
        Assert.Equal(1, results[1].Commits.Single().DeleteBefore, "入力欄にすでに届いた1文字目だけ消す");
        Assert.Equal("k", results[1].Commits.Single().Expect, "TSFが削除できるよう実際に届いた文字を照合する");
        Assert.Equal("か", results[1].View?.Text, "ローマ字を変換中のかなにする");
        Assert.True(!session.Direct, "英数状態を日本語入力に切り替える");
    }

    [Test]
    public static void YenKey_Backslash_ReplacesYenCharacter()
    {
        // 設定「¥ キーで入力する文字」が \ なら、¥ の文字で届くキー (Mac の JIS 配列の ¥ キー) も \ にする。
        MeltypeSession Create(YenKeyStyle style, bool direct = false, bool enabled = true)
        {
            var settings = new Settings { YenKey = style, Enabled = enabled, SigilWordsDirect = false };
            return new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
                new CompositionOptions { YenKey = () => style }, () => settings) { Direct = direct };
        }
        const int OtherCharacterKey = 0x07;
        SessionResult Yen(MeltypeSession session, bool option = false) => session.HandleKey(OtherCharacterKey, '¥', false, false, option, false);

        // 既定: 今までどおり、打ったままアプリへ渡す
        var result = Yen(Create(YenKeyStyle.Yen));
        Assert.True(!result.Consumed && result.Commits.Count == 0 && result.View is null, "既定は ¥ のままアプリへ渡す");

        // \: 日本語入力では、\ として変換ボックスに入る (半角のまま)
        var japanese = Create(YenKeyStyle.Backslash);
        Type(japanese, "kore");
        result = Yen(japanese);
        Assert.True(result.Consumed, "¥ のキーはアプリへ渡さない");
        Assert.Equal("これ\\", result.View?.Text, "\\ として入力中の文字に入る");

        // \: 英数 (直接入力) では、¥ を渡さずに \ を入れる
        result = Yen(Create(YenKeyStyle.Backslash, direct: true));
        Assert.True(result.Consumed, "英数でも ¥ のキーはアプリへ渡さない");
        Assert.Equal("\\", string.Concat(result.Commits.Select(edit => edit.Text)), "英数では \\ を入れる");
        Assert.True(result.Commits.All(edit => edit.DeleteBefore == 0), "何も消さない");

        // Option と一緒のキー・Meltype が OFF のときは触らない
        result = Yen(Create(YenKeyStyle.Backslash), option: true);
        Assert.True(!result.Consumed && result.Commits.Count == 0, "Option+¥ はそのまま");
        result = Yen(Create(YenKeyStyle.Backslash, enabled: false));
        Assert.True(!result.Consumed && result.Commits.Count == 0, "OFF のときはそのまま");
    }

    [Test]
    public static void TsfDirectMode_RomajiDetectionCanBeDisabled()
    {
        var settings = new Settings { DirectModeAutoDetect = false };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { ClassifyDirect = (_, _) => Detection.Verdict.Japanese },
            () => settings, directModeDetection: true)
        {
            Direct = true,
        };

        var results = Type(session, "ka");

        Assert.True(results.All(result => !result.Consumed && result.View is null && result.Commits.Count == 0),
            "設定 OFF ならローマ字判定や文字の置換をしない");
        Assert.True(session.Direct, "英数状態を維持する");
    }

    [Test]
    public static void TsfDirectMode_ManualJapaneseToggleResumesNormalComposition()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        long now = 0;
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions
            {
                ClassifyDirect = (_, _) => Detection.Verdict.Undecided,
            },
            () => settings, directModeDetection: true, timeMs: () => now)
        {
            Direct = true,
        };

        Assert.True(!Type(session, "k")[0].Consumed, "英数状態で判定中のキーは入力欄へ渡す");

        session.Direct = false;
        now += 1000;
        var japanese = Type(session, "ai");

        Assert.True(japanese.All(result => result.Consumed), "手動で日本語に戻した後は通常のローマ字入力として扱う");
        Assert.Equal("あい", japanese[^1].View?.Text, "古い判定中の英字が後から英語判定されない");
    }

    [Test]
    public static void TsfDirectMode_ResumesDetectionAfterUnsentEnglishWordBoundary()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions
            {
                ClassifyDirect = (letters, _) => letters.Equals("ka", StringComparison.OrdinalIgnoreCase) ? Detection.Verdict.Japanese
                    : letters.Length >= 3 ? Detection.Verdict.English
                    : Detection.Verdict.Undecided,
            },
            () => settings, directModeDetection: true)
        {
            Direct = true,
        };

        var english = Type(session, "hello");
        Assert.True(english.All(result => !result.Consumed && result.View is null && result.Commits.Count == 0),
            "英語と分かった語の続きはそのまま入力する");
        Assert.True(session.Direct, "英語の語では英数状態を維持する");

        // TSFは普通の空白をMeltypeに送らずアプリへ直接渡す。次のキーでキャレット前の空白を見て検知を再開する。
        var japanese = Type(session, "ka", "hello ");
        Assert.True(japanese[1].Consumed, "区切りの後の語は日本語判定され、アプリへ素通ししない");
        Assert.True(!session.Direct, "ローマ字の語は日本語入力に切り替える");
    }

    [Test]
    public static void TsfDirectMode_StopsCheckingAfterIdleTimeout()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        long now = 0;
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { ClassifyDirect = (_, _) => Detection.Verdict.Undecided },
            () => settings, directModeDetection: true, timeMs: () => now)
        {
            Direct = true,
        };

        var first = Type(session, "k")[0];
        now += 750;
        var second = Type(session, "a")[0];

        Assert.True(!first.Consumed && !second.Consumed, "無入力 0.7 秒後は英数のまま続ける");
        Assert.True(session.Direct, "英数状態を維持する");
    }

    [Test]
    public static void TsfDirectMode_HyphenatedRomajiDeletesExactReplayedText()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        foreach (var (word, expectedDelete, expectedText) in new[] { ("ro-", 2, "ro"), ("e-ma", 3, "e-m") })
        {
            var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
                new CompositionOptions
                {
                    ClassifyDirect = (letters, _) => letters == "ma" ? Detection.Verdict.Japanese : Detection.Verdict.Undecided,
                },
                () => settings, directModeDetection: true)
            {
                Direct = true,
            };

            var result = Type(session, word)[^1];

            Assert.True(result.Consumed, $"{word}: 日本語と判定したキーを再送しない");
            Assert.Equal(expectedDelete, result.Commits.Single().DeleteBefore, $"{word}: 入力済みの文字数だけ削除する");
            Assert.Equal(expectedText, result.Commits.Single().Expect, $"{word}: 入力欄の実際の文字列を照合する");
        }
    }

    [Test]
    public static void TsfDirectMode_CaretMoveDropsHeldTextAndReplayHistory()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions
            {
                ClassifyDirect = (letters, _) => letters == "wa" ? Detection.Verdict.Japanese : Detection.Verdict.Undecided,
            },
            () => settings, directModeDetection: true)
        {
            Direct = true,
        };

        Type(session, "k");
        session.ForgetDirectInput();
        var result = Type(session, "wa", before: "")[^1];

        Assert.True(result.Consumed, "キャレット移動後のローマ字を日本語と判定する");
        Assert.Equal(1, result.Commits.Single().DeleteBefore, "移動前の文字を削除対象に含めない");
        Assert.Equal("w", result.Commits.Single().Expect, "移動前に判定中だった文字を照合対象に含めない");
    }

    [Test]
    public static void TsfDirectMode_EmptyCaretAfterEnglishWordRestartsDetection()
    {
        var settings = new Settings { DirectModeAutoDetect = true };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions
            {
                ClassifyDirect = (letters, _) => letters == "ka" ? Detection.Verdict.Japanese
                    : letters.Length >= 3 ? Detection.Verdict.English
                    : Detection.Verdict.Undecided,
            },
            () => settings, directModeDetection: true)
        {
            Direct = true,
        };

        Type(session, "hello");
        Assert.True(session.DirectEnglishWord, "英語と判定した語を記録する");
        session.ForgetDirectInput();
        var result = Type(session, "ka", before: "")[^1];

        Assert.True(result.Consumed, "空になった入力欄でも次の語をローマ字判定する");
        Assert.Equal("か", result.View?.Text, "日本語の変換中に切り替わる");
    }

    [Test]
    public static void KeysOutsideComposition_GoToTheApp()
    {
        var session = Create();
        Assert.True(!session.HandleKey(VirtualKeys.Left, null, false, false, false, false).Consumed, "変換ボックスが空なら矢印はアプリへ");
        Assert.True(!session.HandleKey('C', 'c', false, false, false, true).Consumed, "Command + C はアプリの操作");
        Assert.True(!Type(session, " ")[0].Consumed, "空白はアプリへ");
        session.Direct = true;
        Assert.True(!Type(session, "a")[0].Consumed, "英数 (直接入力) ならすべてアプリへ");
    }

    [Test]
    public static void AutoCorrect_ReplacesPreviousWord()
    {
        // i を確定したあと、want で英文と分かったら i を確定し直す (前の文字を消して入れ直す)
        var session = Create();
        Type(session, "i ");
        var results = Type(session, "want ");
        Assert.True(results.SelectMany(r => r.Commits).Any(c => c.DeleteBefore > 0), "前の語を確定し直す");
    }

    [Test]
    public static void AutoCorrect_TellsWhatToDelete()
    {
        // 確定し直すときは、消す文字 (前に確定した文字) も渡す。DLL は入力欄の文字が同じときだけ消す
        var session = Create();
        // i の確定 (Space で変換したものは、次の w で確定する) → want の確定のときに確定し直す
        var commits = Type(session, "i want ").SelectMany(r => r.Commits).ToList();
        var index = commits.FindIndex(c => c.DeleteBefore > 0);
        Assert.True(index > 0, "前の語を確定し直す");
        var first = commits[0].Text;
        var correction = commits[index];
        Assert.Equal(first, correction.Expect);
        Assert.Equal(first.Length, correction.DeleteBefore);
        Assert.True(new SessionResult(true, [correction], null).ToJson().Contains("\"expect\":"), "JSON にも入れる");
    }

    [Test]
    public static void Predictions_SentToNativeSide()
    {
        // Meltype IME の DLL も予測変換の候補の一覧を出せるように、候補と Tab で選んでいる候補を JSON に入れる
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { Predictor = new Predictor(null, null, null), Predictions = () => true }, () => new Settings());
        var typing = Type(session, "decis", "I made ")[^1];
        Assert.True(typing.View?.Predictions?.Contains("decision") == true, string.Join(",", typing.View?.Predictions ?? []));
        Assert.True(typing.ToJson().Contains("\"predictions\":[\"") && typing.ToJson().Contains("\"selectedPrediction\":-1"), typing.ToJson());
        var tab = session.HandleKey(VirtualKeys.Tab, null, false, false, false, false, "I made ");
        Assert.True(tab.Consumed, "Tab は予測の候補を選ぶのに使う");
        Assert.Equal(0, tab.View?.SelectedPrediction);
        Assert.Equal(typing.View!.Predictions![0], tab.View?.Text);
        Assert.True(tab.ToJson().Contains("\"selectedPrediction\":0"), tab.ToJson());
        var back = session.HandleKey(VirtualKeys.Tab, null, true, false, false, false, "I made ");
        Assert.Equal(-1, back.View?.SelectedPrediction, "Shift+Tab で前に戻る (最初の前は選ばない状態)");
    }

    [Test]
    public static void SelectPrediction_Commits()
    {
        // Meltype IME の候補の一覧で予測変換の候補をクリックしたら、その候補を確定する
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { Predictor = new Predictor(null, null, null), Predictions = () => true }, () => new Settings());
        var predictions = Type(session, "decis", "I made ")[^1].View!.Predictions!;
        var result = session.SelectPrediction(1);
        Assert.Equal(predictions[1], result.Commits.Single().Text);
        Assert.True(result.View is null, "確定したら一覧を閉じる");
        Assert.True(session.SelectPrediction(0).Commits.Count == 0, "打っていないときは何もしない");
    }

    [Test]
    public static void Commits_WithoutDeleteHaveNoExpect()
    {
        var session = Create();
        var commit = Type(session, "kyouha\n")[^1].Commits.Single();
        Assert.True(commit.Expect is null, "消さない確定には付けない");
        Assert.True(!new SessionResult(true, [commit], null).ToJson().Contains("expect"), "JSON にも入れない");
    }

    [Test]
    public static void AutoCorrect_NotAfterKeyPassedToApp()
    {
        // 確定したあと、アプリに渡したキー (矢印など) でキャレットが動いたかもしれない: 消す位置がずれるので確定し直さない
        // Enter で確定したあとも、次の語で確定し直す (動かさなければ)
        var control = Create();
        Type(control, "i\n");
        Assert.True(Type(control, "want ").SelectMany(r => r.Commits).Any(c => c.DeleteBefore > 0), "動かさなければ確定し直す");

        var session = Create();
        Type(session, "i\n");
        var left = session.HandleKey(VirtualKeys.Left, null, false, false, false, false);
        Assert.True(!left.Consumed, "変換していないときの矢印はアプリに渡す");
        var results = Type(session, "want ");
        Assert.True(!results.SelectMany(r => r.Commits).Any(c => c.DeleteBefore > 0), "関係ない文字を消さない");
    }

    [Test]
    public static void AutoCorrect_NotAfterCaretMovedOutside()
    {
        // OS の IME がアプリに通したキー・クリックで動いたと知らせてきた (Meltype IME の "moved")
        var session = Create();
        Type(session, "i\n");
        session.ForgetLastCommit();
        var results = Type(session, "want ");
        Assert.True(!results.SelectMany(r => r.Commits).Any(c => c.DeleteBefore > 0), "関係ない文字を消さない");
    }

    [Test]
    public static void ShortcutWhileComposing_CommitsThenPassesTheKey()
    {
        var session = Create();
        Type(session, "abc");
        var result = session.HandleKey('S', 's', false, false, false, true);
        Assert.True(!result.Consumed, "Command + S はアプリへ");
        Assert.True(result.Commits.Count == 1, "その前に変換ボックスの内容を確定する");
    }

    [Test]
    public static void CtrlHWhileComposing_IsBackSpace()
    {
        // #150: Meltype IME・Mac・Linux も、入力中の Ctrl+H は確定せずに 1 音消す
        var session = Create();
        Type(session, "aiueo");
        var result = session.HandleKey('H', '\b', false, true, false, false);
        Assert.True(result.Consumed && result.Commits.Count == 0, "確定しない");
        Assert.Equal("あいうえ", result.View?.Text);
    }

    [Test]
    public static void CtrlUiopWhileComposing_SwitchesKanaAndLetters()
    {
        // #60: Meltype IME・Mac・Linux も、入力中の Ctrl+U/I/O/P は確定せずに F6/F7/F10/F9 と同じ切り替えをする
        var session = Create();
        Type(session, "aiueo");
        foreach (var (key, expected) in new[] { ('P', "ａｉｕｅｏ"), ('O', "aiueo"), ('I', "アイウエオ"), ('U', "あいうえお") })
        {
            var result = session.HandleKey(key, null, false, true, false, false);
            Assert.True(result.Consumed && result.Commits.Count == 0, $"Ctrl+{key} で確定しない");
            Assert.Equal(expected, result.View?.Text, $"Ctrl+{key}");
        }
    }

    [Test]
    public static void ShiftSpace_WhenEmpty_InsertsFullWidthSpace()
    {
        // #246: Meltype IME も、何も打っていないときの Shift+Space は全角スペース (英数のときは半角のままアプリへ)
        var session = Create();
        var result = session.HandleKey(VirtualKeys.Space, ' ', true, false, false, false, "");
        Assert.True(result.Consumed, "Shift+Space を受け取る");
        Assert.Equal("　", string.Concat(result.Commits.Select(c => c.Text)));

        session.Direct = true;
        result = session.HandleKey(VirtualKeys.Space, ' ', true, false, false, false, "");
        Assert.True(!result.Consumed && result.Commits.Count == 0, "英数ではアプリに通す");
    }

    [Test]
    public static void FullWidthSpaceSetting_SpaceIsFullWidthAndShiftSpaceIsHalfWidth()
    {
        // #881: 設定「スペースを全角で入力」では、Meltype IME も何も打っていないときの Space が全角・Shift+Space が半角 (Meltype キーボードと同じ: #259)
        var settings = new Settings { FullWidthSpaceByDefault = true };
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { FullWidthSpaceByDefault = () => settings.FullWidthSpaceByDefault }, () => settings);
        var result = session.HandleKey(VirtualKeys.Space, ' ', false, false, false, false, "");
        Assert.True(result.Consumed, "Space を受け取る");
        Assert.Equal("　", string.Concat(result.Commits.Select(c => c.Text)));

        result = session.HandleKey(VirtualKeys.Space, ' ', true, false, false, false, "");
        Assert.True(result.Consumed, "Shift+Space を受け取る");
        Assert.Equal(" ", string.Concat(result.Commits.Select(c => c.Text)));

        session.Direct = true;
        result = session.HandleKey(VirtualKeys.Space, ' ', false, false, false, false, "");
        Assert.True(!result.Consumed && result.Commits.Count == 0, "英数ではアプリに通す");

        // 設定が OFF なら、Space はアプリに通す (DLL も受け取らない)
        settings.FullWidthSpaceByDefault = false;
        session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { FullWidthSpaceByDefault = () => settings.FullWidthSpaceByDefault }, () => settings);
        result = session.HandleKey(VirtualKeys.Space, ' ', false, false, false, false, "");
        Assert.True(!result.Consumed && result.Commits.Count == 0, "設定が OFF なら通す");
    }

    [Test]
    public static void RoleKeys_OnlyWhenSettingIsOnAndComposing()
    {
        // #199: Linux の IBus・fcitx5 は、入力中の 変換・無変換・ひらがな/カタカナ を先に本体に渡す (meltype_role_key)。
        // 設定が OFF か、何も打っていなければ null (今までどおり英数 / 日本語の切り替えに使う)
        var settings = new Settings();
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { SeparateKeyRoles = () => settings.SeparateKeyRoles }, () => settings);
        Type(session, "kana");
        Assert.True(session.HandleRoleKey(VirtualKeys.NonConvert) is null, "設定が OFF");
        settings.SeparateKeyRoles = true;
        session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { SeparateKeyRoles = () => settings.SeparateKeyRoles }, () => settings);
        Assert.True(session.HandleRoleKey(VirtualKeys.NonConvert) is null, "何も打っていない");
        Type(session, "kana");
        var result = session.HandleRoleKey(VirtualKeys.NonConvert);
        Assert.True(result is { Consumed: true } && result.Commits.Count == 0, "変換ボックスで処理する");
        Assert.Equal("kana", result!.View?.Text);
        Assert.Equal("カナ", session.HandleRoleKey(VirtualKeys.Kana) is { } kana && session.HandleRoleKey(VirtualKeys.Kana) is { } katakana ? katakana.View?.Text : null);
    }

    [Test]
    public static void ArrowWhileComposing_SelectsClauses()
    {
        var session = Create();
        Type(session, "kyouha");
        var result = session.HandleKey(VirtualKeys.Right, null, false, false, false, false);
        Assert.True(result.Consumed && result.View is { Converting: true }, "変換前の矢印は文節の選択に使う");
    }

    [Test]
    public static void Candidates_CanBeSelectedByIndex()
    {
        var session = Create();
        Type(session, "api ");
        var view = session.SelectCandidate(2).View!;
        Assert.Equal(2, view.SelectedIndex);
        var commit = Type(session, "\n")[0];
        Assert.Equal(view.Candidates[2], commit.Commits.Single().Text);
    }

    [Test]
    public static void Reconvert_StartsConversionWithoutCommitting()
    {
        var session = Create();
        var result = session.Reconvert("今日", "きょう");
        Assert.True(result.Consumed, "再変換を始めたらキーは使う");
        Assert.True(session.IsComposing, "変換中になる");
        Assert.True(result.View is { Converting: true } view && view.Text.Length > 0, "変換中の文字を出す");
        Assert.Equal(0, result.Commits.Count);
    }

    [Test]
    public static void Reconvert_EnterCommitsTheConvertedText()
    {
        var session = Create();
        session.Reconvert("今日", "きょう");
        // 元の文字のままなら確定しないので、候補を 1 つ選び直してから確定する
        var shown = session.HandleKey(VirtualKeys.Down, null, false, false, false, false).View!;
        Assert.True(shown.Text != "今日", "候補を選び直した");
        var enter = Type(session, "\n")[0];
        Assert.True(enter.Consumed, "Enter は確定に使う");
        Assert.Equal(shown.Text, enter.Commits.Single().Text);
        Assert.Equal(0, enter.Commits.Single().DeleteBefore);
        Assert.True(!session.IsComposing, "確定したら変換中ではない");
        Assert.True(enter.View is null, "変換ボックスを閉じる");
    }

    [Test]
    public static void Reconvert_CandidateSelectionCommitsTheChosenCandidate()
    {
        var session = Create();
        var view = session.Reconvert("今日", "きょう").View!;
        Assert.True(view.Candidates.Count > 1, "候補が複数ある");
        var chosen = session.SelectCandidate(1).View!;
        var enter = Type(session, "\n")[0];
        Assert.Equal(chosen.Candidates[1], enter.Commits.Single().Text);
    }

    [Test]
    public static void Reconvert_StartsWithTheOriginalTextWhenReadingDoesNotConvertToIt()
    {
        // 読みの推定がずれて (日本語 → にっぽんご) 変換結果が元の文字にならなくても、元の文字が選ばれている。
        var session = Create();
        var view = session.Reconvert("日本語", "にっぽんご").View!;
        Assert.Equal("日本語", view.Text);
        Assert.Equal("ニッポン語", view.Candidates[1]);
        // 元の文字のまま確定したときは確定を返さない (呼び出し側が元の文字を入れ直す。取り消しと同じ)
        var enter = Type(session, "\n")[0];
        Assert.Equal(0, enter.Commits.Count);
        Assert.True(enter.View is null && !session.IsComposing, "変換ボックスを閉じる");
    }

    [Test]
    public static void Reconvert_EscapeEndsWithoutCommit()
    {
        var session = Create();
        session.Reconvert("今日", "きょう");
        var escape = session.HandleKey(VirtualKeys.Escape, null, false, false, false, false);
        Assert.True(escape.Consumed, "Esc は取り消しに使う");
        Assert.Equal(0, escape.Commits.Count);
        Assert.True(escape.View is null, "変換ボックスを閉じる");
        Assert.True(!session.IsComposing, "取り消したら変換中ではない");
        // 取り消したあとも普通に入力できる
        Assert.True(Type(session, "kyou").All(r => r.Consumed), "次の入力は変換ボックスへ");
    }

    [Test]
    public static void Reconvert_DeletingTheWholeReadingEndsWithoutCommit()
    {
        var session = Create();
        session.Reconvert("今日", "きょう");
        var commits = 0;
        SessionResult? last = null;
        for (var i = 0; i < 10 && session.IsComposing; i++)
        {
            last = session.HandleKey(VirtualKeys.Back, null, false, false, false, false);
            commits += last.Commits.Count;
        }
        Assert.True(!session.IsComposing, "読みを全部消したら終わる");
        Assert.Equal(0, commits);
        Assert.True(last?.View is null, "変換ボックスを閉じる");
    }

    [Test]
    public static void Reconvert_FocusLossCommitsTheReplacement()
    {
        var session = Create();
        session.Reconvert("今日", "きょう");
        var shown = session.HandleKey(VirtualKeys.Down, null, false, false, false, false).View!;
        var commit = session.CommitPending();
        Assert.Equal(shown.Text, commit.Commits.Single().Text);
        Assert.True(!session.IsComposing, "確定したら変換中ではない");
    }

    [Test]
    public static void Reconvert_InvalidInputDoesNotStart()
    {
        foreach (var (text, reading) in new[] { ("", ""), ("今日", ""), ("今日", "  "), ("", "きょう"), ("今\n日", "きょう"), (new string('あ', 129), new string('あ', 129)) })
        {
            var session = Create();
            var result = session.Reconvert(text, reading);
            Assert.True(!result.Consumed, $"始めない: {text.Length}/{reading}");
            Assert.True(result.View is null && result.Commits.Count == 0 && !session.IsComposing, "何も出さない");
        }
    }

    [Test]
    public static void Reconvert_DoesNothingWhileComposingOrDirect()
    {
        var session = Create();
        Type(session, "kyou");
        var result = session.Reconvert("今日", "きょう");
        Assert.True(!result.Consumed && result.Commits.Count == 0, "変換中は始めない");
        Assert.True(session.IsComposing, "打っていた入力は変わらない");
        session = Create();
        session.Direct = true;
        Assert.True(!session.Reconvert("今日", "きょう").Consumed, "英数では始めない");
    }

    [Test]
    public static void Json_IsEscaped()
    {
        var result = new SessionResult(true, [new TextEdit(2, "a\"b\\c\n")], new CompositionView("x", ["y"], 0, true, "h", ["x"], 0));
        const string expected = """{"consumed":true,"commits":[{"deleteBefore":2,"text":"a\"b\\c\n"}],"view":{"text":"x","committed":null,"converting":true,"selectedIndex":0,"selectedClause":0,"hint":"h","candidates":["y"],"clauses":["x"],"suggestion":null,"meaning":null,"notes":[null],"predictions":[],"selectedPrediction":-1,"typed":null}}""";
        Assert.Equal(expected, result.ToJson());
    }

    [Test]
    public static void Json_HasTypedKeys_WhenSettingIsOn()
    {
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { ShowTypedKeys = () => true }, () => new Settings());
        var json = Type(session, "kyou")[^1].ToJson();
        Assert.True(json.Contains("\"typed\":\"kyou\""), json);
    }

    [Test]
    public static void CodeInput_PassesCodeButComposesCommentsAndStrings()
    {
        var session = Create();
        session.CodeInput = true;
        Assert.True(Type(session, "kyouha", "const value = ").All(r => !r.Consumed), "コードは英数のまま");
        Assert.True(Type(session, "hello").All(r => !r.Consumed), "周辺文字列が取得できない場合も英数");
        Assert.Equal("きょうは", Type(session, "kyouha", "// ")[^1].View?.Text);
        Type(session, "\n");
        Assert.Equal("きょうは", Type(session, "kyouha", "const text = \"")[^1].View?.Text);
        Type(session, "\n");
        Assert.Equal("きょうは", Type(session, "kyouha", "/* comment\n")[^1].View?.Text);
    }

    [Test]
    public static void CodeInput_SetCodeLineMakesCodeJapaneseUntilCaretMoves()
    {
        var session = Create();
        session.CodeInput = true;
        const string line = "int x = 1;\nfoo(";
        Assert.True(session.CodeEnglishAt(line), "コードの行は英数");
        Assert.True(session.SetCodeLine(true, line), "コードの行を日本語にする");
        Assert.True(!session.CodeEnglishAt(line), "日本語にした");
        Assert.Equal("きょうは", Type(session, "kyouha", line)[^1].View?.Text);
        Type(session, "\n");
        session.CaretMoved(VirtualKeys.Return, false);
        Assert.True(!session.CodeEnglishAt(line + "今日は\n"), "改行しても日本語のまま");
        Assert.Equal("きょうは", Type(session, "kyouha", line + "今日は\n")[^1].View?.Text);
        Type(session, "\n");
        session.CaretMoved('A', false);
        Assert.True(!session.CodeEnglishAt("foo(a"), "英字を打っただけなら日本語のまま");
        session.CaretMoved(0x26, false);
        Assert.True(session.CodeEnglishAt(line), "矢印で別の場所に移ったら英数に戻す");
        Assert.True(Type(session, "hello", line).All(r => !r.Consumed), "英数に戻した");

        Assert.True(session.SetCodeLine(true, line), "もう一度日本語にする");
        session.CaretMoved(0, false);
        Assert.True(session.CodeEnglishAt(line), "クリックで英数に戻す");
        Assert.True(session.SetCodeLine(true, line), "もう一度日本語にする");
        session.CaretMoved('V', true);
        Assert.True(session.CodeEnglishAt(line), "Ctrl の操作で英数に戻す");
        Assert.True(session.SetCodeLine(true, line), "もう一度日本語にする");
        Assert.True(session.SetCodeLine(false, line), "半角/全角 で英数に戻す");
        Assert.True(!session.SetCodeLine(false, line), "日本語にしていなければ、英数に戻すことはない (IME を閉じる)");

        session.CodeTerminal = true;
        Assert.True(session.SetCodeLine(true, "$ "), "ターミナル");
        session.CaretMoved(0, false);
        Assert.True(!session.CodeEnglishAt("$ "), "ターミナルは出力でキャレットが動いても日本語のまま");
        session.CaretMoved(0x26, false);
        Assert.True(session.CodeEnglishAt("$ "), "矢印で英数に戻す");
        Assert.True(session.SetCodeLine(true, "$ "), "ターミナル");
        session.CodeTerminal = false;
        Assert.True(session.CodeEnglishAt(line), "エディターとターミナルを行き来したら英数に戻す");

        Assert.True(!session.CodeEnglishAt("// "), "コメントの中は日本語");
        Assert.True(!session.SetCodeLine(true, "// "), "コメントの中はもう日本語");
        session.CodeInput = false;
        Assert.True(!session.CodeEnglishAt(line), "コードの入力欄でなければ日本語");
        Assert.True(!session.SetCodeLine(true, line), "コードの入力欄でなければ IME の ON/OFF");
    }

    [Test]
    public static void CodeInput_ClosingQuoteStaysAscii()
    {
        var session = Create();
        session.CodeInput = true;
        var empty = Type(session, "\"", "x = \"")[0];
        Assert.True(!empty.Consumed && empty.Commits.Count == 0, "空の文字列を閉じる引用符は英数のまま通す");
        Assert.Equal("きょうは", Type(session, "kyouha", "x = \"")[^1].View?.Text);
        var close = Type(session, "\"")[0];
        Assert.True(!close.Consumed && close.Commits.Single().Text == "きょうは", "閉じる引用符は英数のまま通し、日本語は確定");
        Assert.True(session.CodeEnglishAt(null), "閉じたあとは英数");
        Assert.True(Type(session, "abc", "x = \"今日は\"").All(r => !r.Consumed), "閉じたあとのキーは英数のまま通す");
    }

    [Test]
    public static void MixedInput_ConvertsJapaneseAndKeepsEnglish()
    {
        var session = Create();
        Type(session, "kyouhagoogledekensaku ");
        var result = Type(session, "\n")[0];
        Assert.Equal("今日はgoogleで検索", result.Commits.Single().Text);
    }

    [Test]
    public static void CodeInput_TracksTerminalQuotesWithoutSurroundingText()
    {
        var session = Create();
        session.CodeInput = true;
        Assert.True(Type(session, "echo \"").All(r => !r.Consumed), "シェルのコマンドは直接入力");
        Assert.Equal("きょうは", Type(session, "kyouha")[^1].View?.Text);
        var close = Type(session, "\"")[0];
        Assert.True(!close.Consumed && close.Commits.Single().Text == "きょうは", "閉じ引用符は英数のまま通し、日本語は確定");
        Assert.True(Type(session, "hello").All(r => !r.Consumed), "文字列の外では英数に戻す");
        session.CodeInput = true;
        Type(session, "# ");
        Assert.Equal("きょうは", Type(session, "kyouha")[^1].View?.Text);
    }

    [Test]
    public static void CodeInput_ActivationSyncClearsTrackedContext()
    {
        var session = Create();
        session.CodeInput = true;
        Assert.True(Type(session, "echo \"").All(r => !r.Consumed), "引用符の開始まではアプリへ渡す");
        session.CommitPending();

        // 同じ入力欄の再有効化 (Mac など): 周辺文字列がなくても、前の引用符を引き継がない。
        session.Direct = false;
        session.CodeInput = true;
        var afterQuote = Type(session, "j").Single();
        Assert.True(!afterQuote.Consumed && afterQuote.View is null && afterQuote.Commits.Count == 0,
            "同じコード入力の設定も、既存の呼び出し元では文脈の初期化として使う");

        Assert.True(session.SetCodeLine(true, null), "明示的な日本語指定を有効にする");
        session.CommitPending();
        session.Direct = false;
        session.CodeInput = true;
        var afterJapanese = Type(session, "k").Single();
        Assert.True(!afterJapanese.Consumed && afterJapanese.View is null && afterJapanese.Commits.Count == 0,
            "再有効化で明示的な日本語指定も初期化する");
    }

    [Test]
    public static void CodeInput_TracksCommentSlashesWithSigilWordsEnabled()
    {
        var session = Create();
        session.CodeInput = true;
        Assert.True(Type(session, "// ").All(r => !r.Consumed), "コメントの開始は直接入力");
        Assert.Equal("きょうは", Type(session, "kyouha")[^1].View?.Text);
    }

    [Test]
    public static void EnglishSentence_RemainsEnglish()
    {
        var session = Create();
        var results = Type(session, "I want to go to the park\n");
        Assert.Equal("I want to go to the park", string.Concat(results.SelectMany(r => r.Commits).Select(e => e.Text)));
    }

    [Test]
    public static void JoinedFarewell_RemainsEnglish()
    {
        var session = Create();
        var results = Type(session, "seeyouagain\n");
        Assert.Equal("seeyouagain", string.Concat(results.SelectMany(r => r.Commits).Select(e => e.Text)));
    }

    [Test]
    public static void Go_OffersEnglishAndHiraganaWhileTyping()
    {
        foreach (var (index, expected) in new[] { (0, "go"), (1, "ご") })
        {
            var session = Create();
            var view = Type(session, "go")[^1].View!;
            Assert.True(view.Candidates.SequenceEqual(new[] { "go", "ご" }), "入力中に両方の候補を返す");
            Assert.Equal(expected, session.SelectCandidate(index).View?.Text);
            Assert.Equal(expected, Type(session, "\n")[0].Commits.Single().Text);
        }
        var cycling = Create();
        Type(cycling, "go");
        Assert.Equal("go", Type(cycling, " ")[0].View?.Text);
        Assert.Equal("ご", Type(cycling, " ")[0].View?.Text);
    }

    [Test]
    public static void GoPreview_IsLimitedToExactAutomaticToken()
    {
        foreach (var input in new[] { "g", "goo", "google", "gohan", "kyouhago" })
        {
            var session = Create();
            var view = Type(session, input)[^1].View!;
            Assert.True(view.Candidates.Count == 0, input + ": go preview must not replace other candidates");
            Assert.True(!session.SelectCandidate(0).View!.Converting, input + ": no preview selection");
        }
        var forced = Create();
        Type(forced, "go");
        var kana = forced.HandleKey(VirtualKeys.F6, null, false, false, false, false).View!;
        Assert.Equal("ご", kana.Text);
        Assert.Equal(0, kana.Candidates.Count);
        var invalid = Create();
        Type(invalid, "go");
        Assert.True(!invalid.SelectCandidate(2).View!.Converting, "invalid preview index leaves composition unchanged");
    }

    [Test]
    public static void EnglishVerb_ConjugationDoesNotCrossRomajiTokens()
    {
        foreach (var (input, expected) in new[] {
            ("reflectsareta", "reflectされた"), ("reflectsita", "reflectした"),
            ("reflectsaseru", "reflectさせる"), ("reflected", "reflected"), ("reflects", "reflects") })
        {
            var detector = CompositionDetector.CreateDefault();
            detector.SpellChecker = Meltype.Detection.BuiltInWordChecker.Shared;
            var session = new MeltypeSession(detector, new CompositionTests.FakeConverter(), new CompositionOptions(), () => new Settings());
            var results = Type(session, input + "\n");
            Assert.Equal(expected, string.Concat(results.SelectMany(r => r.Commits).Select(e => e.Text)));
        }
    }

    [Test]
    public static void Misspelling_IsExposedAndCorrected()
    {
        var session = new MeltypeSession(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { Misspellings = MisspellingDictionary.Load(null) }, () => new Settings());
        Assert.True(Type(session, "buresureddo")[^1].View?.Suggestion?.Contains("ブレスレット") == true, "Mac に修正候補を返す");
        var result = session.HandleKey(VirtualKeys.Tab, null, false, false, false, false);
        Assert.Equal("ぶれすれっと", result.View?.Text);
    }

    [Test]
    public static void SigilWord_PassesToTheAppUntilSpace()
    {
        // #193: 先頭の @review は打つたびにアプリへ渡し (補完を選べるように)、空白の後は日本語に戻る。
        // 文章の入力欄の先頭の / は ・ として変換ボックスに入れる ので、@ で確かめる。
        var session = Create();
        Assert.True(Type(session, "@review").All(r => !r.Consumed && r.View is null), "@review はそのままアプリへ");
        Assert.True(!Type(session, " ")[0].Consumed, "空白もアプリへ");
        Assert.Equal("きょう", Type(session, "kyou")[^1].View?.Text);
        // google を確定した後の空白に続く @ も。
        session = Create();
        Type(session, "google ");
        Assert.True(Type(session, "@file").All(r => !r.Consumed), "空白の後の @file はアプリへ");
    }

    [Test]
    public static void SlashAtStart_IsMiddleDotInText()
    {
        // 文章の入力欄の先頭の / は ・。Space で / にして名前を打つと、/ を確定して名前はアプリへ。
        var session = Create();
        Assert.Equal("・", Type(session, "/", before: "")[0].View?.Text);
        Assert.Equal("/", Type(session, " ")[0].View?.Text);
        var help = Type(session, "help");
        Assert.True(help[0].Commits.Single().Text == "/" && help.All(r => !r.Consumed && r.View is null), "/ を確定して help はアプリへ");
        // Discord は空の入力欄でも、キャレットの前に U+FEFF を返す。
        session = Create();
        Assert.Equal("・", Type(session, "/", before: "\uFEFF")[0].View?.Text);
        // /help を送信した後 (Enter は Meltype IME を通らない) の空の欄では、日本語に戻る。
        Type(session, " ");
        Type(session, "help");
        Assert.Equal("きょう", Type(session, "kyou", before: "\uFEFF")[^1].View?.Text);
        // コードの入力欄では、今までどおりアプリへ。
        session = Create();
        session.CodeInput = true;
        Assert.True(Type(session, "/help", before: "").All(r => !r.Consumed), "コードの /help はアプリへ");
        // コードの入力欄でも、コメント・文字列の中は ・。
        session = Create();
        session.CodeInput = true;
        Assert.Equal("・", Type(session, "/", before: "// メモ ")[0].View?.Text);
        session = Create();
        session.CodeInput = true;
        Assert.Equal("・", Type(session, "/", before: "var text = \"メモ ")[0].View?.Text);
        // ターミナルの AI の入力行 (Claude Code の「> 」の後) は /command を打つ所なので、今までどおりアプリへ。
        session = Create();
        session.CodeInput = true;
        Assert.True(!Type(session, "/", before: "> ")[0].Consumed && Type(session, "help").All(r => !r.Consumed), "AI の入力行の /help はアプリへ");
    }

    [Test]
    public static void SigilWord_EndsWhenSpaceReachedTheAppDirectly()
    {
        // #242: Meltype IME は変換中でないときの Space・Enter を Meltype.exe に送らず、アプリに通す。
        // @xyz の後の空白・改行はキャレットの前の文字で分かるので、そこで普通の入力に戻る (ずっと素通しにならない)
        foreach (var before in new[] { "@xyz ", "@xyz\r\n", "@xyz\n" })
        {
            var session = Create();
            Assert.True(Type(session, "@xyz", before: "").All(r => !r.Consumed), "@xyz はアプリへ");
            Assert.Equal("きょう", Type(session, "kyou", before)[^1].View?.Text, before);
        }
        // 名前の途中 (前が名前の文字) なら今までどおりアプリへ
        var typing = Create();
        Type(typing, "@xy", before: "");
        Assert.True(!Type(typing, "z", before: "@xy")[0].Consumed, "名前の続きはアプリへ");
        // 空白の後の 2 つ目の @file も、そのままアプリへ
        typing = Create();
        Type(typing, "@xyz", before: "");
        Assert.True(!Type(typing, "@", before: "@xyz ")[0].Consumed, "空白の後の @ はアプリへ");
    }

    [Test]
    public static void SigilWord_UsesTextBeforeCaret()
    {
        // キャレットの前の文字を教えてもらえば、それで決める (taro@ は対象外、"> " の後は対象)。
        var session = Create();
        Assert.True(Type(session, "@", before: "taro")[0].Consumed, "前が英字なら @ は変換ボックスへ");
        session = Create();
        session.HandleKey(VirtualKeys.Left, null, false, false, false, false);
        Assert.True(!Type(session, "@", before: "> ")[0].Consumed, "前が空白なら @ はアプリへ");
        // 前の文字を打ったのを見ていれば、空 (前の文字を読めないアプリ) より自分の記録を信じる。
        session = Create();
        session.Direct = true;
        Type(session, "taro");
        session.Direct = false;
        Assert.True(Type(session, "@", before: "")[0].Consumed, "taro と打った後の @ は変換ボックスへ");
    }

    private static MeltypeSession CreateMac() =>
        new(CompositionTests.Detector, new CompositionTests.FakeConverter(), new CompositionOptions { ControlKeys = () => ControlKeyStyle.Mac }, () => new Settings { ControlKeys = ControlKeyStyle.Mac });

    [Test]
    public static void MacControl_Arrows_InEnglishWord_CommitThenPassToApp()
    {
        // 英語と判定した語の途中の Ctrl+N などは割り当てなし: 確定して、キーはアプリへ (↓ にならず、文字 n も変換ボックスに入らない)。
        // Linux (IBus/fcitx5) は Ctrl+N でも文字 'n' を渡す
        foreach (var letter in "NPBFWOI")
        {
            var session = CreateMac();
            Type(session, "hello");
            Assert.True(session.IsComposing, "hello を入力中");
            var result = session.HandleKey(letter, char.ToLowerInvariant(letter), false, true, false, false);
            Assert.True(!result.Consumed, $"Ctrl+{letter} はアプリへ渡す: {result.View?.Text} [{string.Join(",", result.Commits.Select(c => c.Text))}]");
            Assert.Equal("hello", string.Concat(result.Commits.Select(c => c.Text)), $"Ctrl+{letter}: 確定する");
            Assert.True(!session.IsComposing, $"Ctrl+{letter}: n などが変換ボックスに入らない");
            Assert.True(result.View is null, "変換ボックスは閉じる");
            // 続けて Ctrl+N / Ctrl+C を押しても、そのままアプリへ
            Assert.True(!session.HandleKey(letter, char.ToLowerInvariant(letter), false, true, false, false).Consumed, "続けて押してもアプリへ");
            Assert.True(!session.HandleKey('C', 'c', false, true, false, false).Consumed, "Ctrl+C もアプリへ");
        }
    }

    [Test]
    public static void MacControl_Arrows_KanaAndConverting_AreUsed()
    {
        // かな入力中 (変換前) と変換中の Ctrl+N/B/F は今までどおり使い切る
        foreach (var letter in "NBF")
        {
            var session = CreateMac();
            Type(session, "tanniwotoru");
            var before = session.HandleKey(letter, char.ToLowerInvariant(letter), false, true, false, false);
            Assert.True(before.Consumed, $"変換前の Ctrl+{letter}: {before.View?.Text} {session.IsComposing}");
            Assert.True(before.View?.Converting == true, $"変換前の Ctrl+{letter} で文節の選択に入る");
        }
        var converting = CreateMac();
        Type(converting, "egao ");
        Assert.True(converting.HandleKey('N', 'n', false, true, false, false).Consumed, "変換中の Ctrl+N");
        Assert.True(converting.IsComposing, "変換中のまま");
        // Ctrl+V / Ctrl+R は変換中に PageDown / PageUp を押したのと同じ
        foreach (var (page, letter) in new[] { (0x22, 'V'), (0x21, 'R') })
        {
            var plain = Create();
            Type(plain, "egao ");
            var expected = plain.HandleKey(page, null, false, false, false, false);
            var mac = CreateMac();
            Type(mac, "egao ");
            var actual = mac.HandleKey(letter, char.ToLowerInvariant(letter), false, true, false, false);
            Assert.Equal(expected.Consumed, actual.Consumed, $"Ctrl+{letter}");
            Assert.Equal(plain.IsComposing, mac.IsComposing, $"Ctrl+{letter}");
            Assert.Equal(expected.View?.SelectedIndex, actual.View?.SelectedIndex, $"Ctrl+{letter}");
        }
    }

    [Test]
    public static void MacControl_ShiftedSymbols_AreNotUsed()
    {
        // 英字以外のキーの Shift 込みの文字: Ctrl+Shift+' (US) の " と Ctrl+Shift+; (JIS) の + は割り当てなし。Ctrl+: (JIS) は半角英字
        foreach (var (vk, ch) in new[] { (0xDE, '"'), (0xBB, '+'), (0xBA, ':') })
        {
            var session = CreateMac();
            Type(session, "aiueo");
            var result = session.HandleKey(vk, ch, shift: ch != ':', control: true, false, false);
            if (ch == ':') Assert.True(result.Consumed && session.IsComposing, $"Shift なしの Ctrl+: は半角英字 {result.Consumed} {session.IsComposing} {result.View?.Text}");
            else Assert.True(!result.Consumed, $"Ctrl+Shift+{ch} はアプリ (英数への切り替え) に回す");
        }
    }

    [Test]
    public static void PrivateField_RemembersNothing()
    {
        // #511: 入力を覚えないように求める入力欄 (ブラウザーのシークレット / InPrivate ウィンドウ) では、選び直した変換を覚えない
        var history = new ConversionHistory(null);
        MeltypeSession Session() => new(CompositionTests.Detector, new CompositionTests.FakeConverter(),
            new CompositionOptions { History = history, Candidates = CandidateDictionary.Load(null) }, () => new Settings { LiveConversion = false });
        void ChooseHashi(MeltypeSession session)
        {
            var view = Type(session, "hashiwo ")[^1].View!;
            var index = view.Candidates.ToList().IndexOf("箸を");
            Assert.True(index > 0, string.Join(",", view.Candidates));
            for (var i = 0; i < index; i++) Type(session, " ");
            Assert.Equal("箸を", Type(session, "\n")[^1].Commits.Single().Text);
        }

        var session = Session();
        session.Private = true;
        ChooseHashi(session);
        Assert.True(history.Get("はしを") is null, "プライベートの入力欄では覚えない");

        // ふつうの入力欄に戻れば覚える
        session.Private = false;
        ChooseHashi(session);
        Assert.Equal("箸を", history.Get("はしを"));
      }
    [Test]
    public static void CreateDefault_SessionsShareLearning()
    {
        // 入力欄 (セッション) が 2 つあっても、学習データは 1 つ。B で選び直して保存しても、A で選び直したものが消えない
        var directory = Path.Combine(Path.GetTempPath(), "meltype-session-share-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> More(string reading) => reading switch
            {
                "かわ" => ["川", "皮", "河"],
                "あつい" => ["熱い", "暑い", "厚い"],
                _ => [],
            };
            var a = MeltypeSession.CreateDefault(directory, new CompositionTests.FakeConverter(), More, null);
            var b = MeltypeSession.CreateDefault(directory, new CompositionTests.FakeConverter(), More, null);
            void Choose(MeltypeSession session, string typed, string word)
            {
                var view = Type(session, typed + " ")[^1].View;
                Assert.True(view?.Candidates.Contains(word) == true, $"{typed}: {string.Join(",", view?.Candidates ?? [])}");
                session.SelectCandidate(view!.Candidates.ToList().IndexOf(word));
                Assert.Equal(word, Type(session, "\n")[0].Commits.Single().Text, typed);
            }
            Choose(a, "kawa", "皮");
            Choose(b, "atsui", "暑い");

            var saved = new ConversionHistory(Path.Combine(directory, "conversions.json"));
            Assert.Equal("皮", saved.Get("かわ"), "A で覚えたものが B の保存で消えない");
            Assert.Equal("暑い", saved.Get("あつい"));
            // 次に作った入力欄でも、A・B で覚えたものを使う
            var c = MeltypeSession.CreateDefault(directory, new CompositionTests.FakeConverter(), More, null);
            Assert.Equal("皮", Type(c, "kawa ")[^1].View?.Candidates[0]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
