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
    public static void EnglishWord_SpaceCommitsWithSpace()
    {
        var session = Create();
        var results = Type(session, "google ");
        Assert.Equal("google ", results[^1].Commits.Single().Text);
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
        const string expected = """{"consumed":true,"commits":[{"deleteBefore":2,"text":"a\"b\\c\n"}],"view":{"text":"x","converting":true,"selectedIndex":0,"selectedClause":0,"hint":"h","candidates":["y"],"clauses":["x"],"suggestion":null,"meaning":null,"notes":[null]}}""";
        Assert.Equal(expected, result.ToJson());
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
        // #193: 先頭の /review は打つたびにアプリへ渡し (補完を選べるように)、空白の後は日本語に戻る。
        var session = Create();
        Assert.True(Type(session, "/review").All(r => !r.Consumed && r.View is null), "/review はそのままアプリへ");
        Assert.True(!Type(session, " ")[0].Consumed, "空白もアプリへ");
        Assert.Equal("きょう", Type(session, "kyou")[^1].View?.Text);
        // google を確定した後の空白に続く @ も。
        session = Create();
        Type(session, "google ");
        Assert.True(Type(session, "@file").All(r => !r.Consumed), "空白の後の @file はアプリへ");
    }

    [Test]
    public static void SigilWord_UsesTextBeforeCaret()
    {
        // キャレットの前の文字を教えてもらえば、それで決める (taro@ は対象外、"> " の後は対象)。
        var session = Create();
        Assert.True(Type(session, "@", before: "taro")[0].Consumed, "前が英字なら @ は変換ボックスへ");
        session = Create();
        session.HandleKey(VirtualKeys.Left, null, false, false, false, false);
        Assert.True(!Type(session, "/", before: "> ")[0].Consumed, "前が空白なら / はアプリへ");
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
}
