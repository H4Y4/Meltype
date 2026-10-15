// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 H4Y4

import Foundation

/// IME として動く Meltype を 1 つだけにする。
///
/// アプリは imklaunchagent (macOS の仲介役) に Meltype の接続先を聞いてつながる。接続先はバンドル ID ごとに 1 つで、
/// IMKServer を作るたびに上書きされ、作った Meltype が終了すると空になる。Meltype が 2 つ動くと
/// (更新の直後に、macOS がアプリの求めで起動した Meltype と start-input-method.sh が起動した Meltype)、
/// 後の方が接続先を上書きし、それが終了したとき、先の Meltype は動いているのに接続先が空になる。
/// その間に打ったアプリは待たされ、次に Meltype が終了したときに外されて、アプリを再起動するまで何も入力できなくなる。
///
/// そこで IMKServer を作る前にロックを取る。取れなければ、IMK には何も登録せずに待つ (終了すると、
/// launchctl submit のジョブは起動し直されるため)。ロックはプロセスが終わると (落ちても) カーネルが外すので、
/// 先の Meltype が終わると、待っていた方が引き継ぐ。
enum ServerLock {
    /// ロックのファイル。どの起動のしかたでも同じ場所になるように、環境変数では変えない。
    static func defaultURL(bundleIdentifier: String) -> URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(bundleIdentifier, isDirectory: true)
            .appendingPathComponent("server.lock")
    }

    /// ロックを取り、開いたファイルを返す (閉じるとロックが外れるので、動いている間は持ち続ける)。
    /// wait が false で、ほかのプロセスが持っているときは nil。
    static func acquire(at url: URL, wait: Bool) -> Int32? {
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        // 子プロセスに引き継がせない (子が残ると、Meltype が終わってもロックが外れない)
        let descriptor = open(url.path, O_RDWR | O_CREAT | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else {
            // ロックのファイルを作れないときは、今までどおり起動する (入力できなくなるよりよい)
            NSLog("Meltype: ロックのファイルを開けませんでした (\(String(cString: strerror(errno)))): \(url.path)")
            return descriptor
        }
        while flock(descriptor, LOCK_EX | (wait ? 0 : LOCK_NB)) != 0 {
            if errno == EINTR { continue }
            close(descriptor)
            return nil
        }
        return descriptor
    }
}
