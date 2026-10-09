// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

import Cocoa
import InputMethodKit
import Carbon.HIToolbox
final class EventClient: NSObject, IMKTextInput {
 var document=""; var marked=""
 func text(_ value:Any?)->String { (value as? NSAttributedString)?.string ?? (value as? String ?? "") }
 func insertText(_ string: Any!, replacementRange: NSRange) { let value=text(string); if replacementRange.location != NSNotFound { document=(document as NSString).replacingCharacters(in:replacementRange,with:value) } else {document+=value}; marked="" }
 func setMarkedText(_ string: Any!, selectionRange: NSRange, replacementRange: NSRange) {marked=text(string)}
 func selectedRange()->NSRange {NSRange(location:(document as NSString).length,length:0)}
 func markedRange()->NSRange {marked.isEmpty ? NSRange(location:NSNotFound,length:0):NSRange(location:(document as NSString).length,length:(marked as NSString).length)}
 func attributedSubstring(from range:NSRange)->NSAttributedString! { let text=document as NSString; guard range.location != NSNotFound && range.location<=text.length else{return nil}; return NSAttributedString(string:text.substring(with:NSRange(location:range.location,length:min(range.length,text.length-range.location)))) }
 func length()->Int {(document as NSString).length}
 func characterIndex(for point:NSPoint, tracking mode:IMKLocationToOffsetMappingMode, inMarkedRange:UnsafeMutablePointer<ObjCBool>!)->Int {inMarkedRange?.pointee=false; return 0}
 func attributes(forCharacterIndex index:Int, lineHeightRectangle rectangle:UnsafeMutablePointer<NSRect>!)->[AnyHashable:Any]! {rectangle?.pointee=NSRect(x:0,y:0,width:1,height:20); return [:]}
 func validAttributesForMarkedText()->[Any]! {[]}
 func overrideKeyboard(withKeyboardNamed name:String!) {}
 func selectMode(_ identifier:String!) {}
 func supportsUnicode()->Bool {true}
 func bundleIdentifier()->String! {"local.meltype.event-client"}
 func windowLevel()->Int32 {0}
 func supportsProperty(_ property:TSMDocumentPropertyTag)->Bool {true}
 func uniqueClientIdentifierString()->String! {"synthetic-event-client"}
 func string(from range:NSRange, actualRange:UnsafeMutablePointer<NSRange>!)->String! {actualRange?.pointee=range;return attributedSubstring(from:range)?.string}
 func firstRect(forCharacterRange range:NSRange, actualRange:UnsafeMutablePointer<NSRange>!)->NSRect {actualRange?.pointee=range;return .zero}
}
#if !REAL_CONVERTER
final class MeltypeConverter {
 static let shared=MeltypeConverter()
 func clauses(for text:String,context:String?)->[(reading:String,text:String)] {text=="つくえ" ? [(reading:text,text:"机")] : []}
 // ページ送りのテスト用に、つくえ だけ 30 個の候補を返す (他の読みは今までどおり空)。
 func candidates(for text:String)->[String] {text=="つくえ" ? (1...30).map {"候補\($0)"} : []}
}
#endif
// 候補ウィンドウの実物 (IMKCandidates) は画面が無いと動かせないので、moveDown / moveUp の回数から選択の位置を数える偽物に差し替える。
// 実物が端で止まるか回るかはここでは確かめられない (InputController.selectInWindow は端を越える動きをしない前提)。
final class RecordingCandidates: IMKCandidates {
 var position=0; var shown=false
 override func update() {position=0}
 override func show(_ hint:IMKCandidatesLocationHint) {shown=true}
 override func hide() {shown=false}
 override func isVisible()->Bool {shown}
 override func moveDown(_ sender:Any?) {position+=1}
 override func moveUp(_ sender:Any?) {position-=1}
}
var candidatesWindow: IMKCandidates? = nil
@main
struct EventTests {
 static func main() {
  let app=NSApplication.shared
  app.setActivationPolicy(.prohibited)
  guard let server=IMKServer(name:"LocalMeltypeBoundaryTests",bundleIdentifier:Bundle.main.bundleIdentifier) else {fatalError("IMKServer creation failed")}
  var passed=0
  func check(_ name:String,_ body:(MeltypeInputController,EventClient)->Void) {
   let client=EventClient()
   // IMKInputController の初期化は OS のクライアントプロキシ専用なので nil で初期化する。
   // handle / commitComposition には合成 IMKTextInput クライアントを明示して渡す。
   guard let controller=MeltypeInputController(server:server,delegate:nil,client:nil) else {fatalError("Controller creation failed")}
   body(controller,client); passed+=1; print("PASS: \(name)")
  }
  func key(_ controller:MeltypeInputController,_ client:EventClient,_ chars:String,code:UInt16=0,flags:NSEvent.ModifierFlags=[])->Bool {
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:flags,timestamp:0,windowNumber:0,context:nil,characters:chars,charactersIgnoringModifiers:chars,isARepeat:false,keyCode:code)!
   let consumed=controller.handle(event,client:client)
   if !consumed && !flags.contains(.command) && !flags.contains(.control) && chars.unicodeScalars.allSatisfy({$0.value>=0x20}) {
    // アプリが元イベントを受ける部分を 1 回だけ模擬する。
    client.insertText(chars,replacementRange:NSRange(location:NSNotFound,length:0))
   }
   return consumed
  }
  // 機能キーの NSEvent。実機では characters に NSUpArrowFunctionKey (U+F700) 〜 NSModeSwitchFunctionKey (U+F747) の
  // 私用領域の文字が入り、アプリはそれを文字として挿入しない。
  func functionKey(_ controller:MeltypeInputController,_ client:EventClient,_ scalar:UInt32,code:Int)->Bool {
   let chars=String(UnicodeScalar(scalar)!)
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[.function],timestamp:0,windowNumber:0,context:nil,characters:chars,charactersIgnoringModifiers:chars,isARepeat:false,keyCode:UInt16(code))!
   return controller.handle(event,client:client)
  }
  func hasFunctionKeyScalar(_ text:String)->Bool { text.unicodeScalars.contains { (0xF700...0xF747).contains($0.value) } }
  func type(_ controller:MeltypeInputController,_ client:EventClient,_ raw:String) {
   for scalar in raw.unicodeScalars { _=key(controller,client,String(scalar),code:UInt16(kVK_ANSI_A)) }
  }
  func equal(_ actual:String,_ expected:String) {
   precondition(Array(actual.unicodeScalars)==Array(expected.unicodeScalars),"expected \(expected.debugDescription), got \(actual.debugDescription)")
  }
  for (raw,event,expected) in [("e","\u{0301}\u{0300}","e\u{0301}\u{0300}"),("ka","\u{3099}\u{FE0F}","か\u{3099}\u{FE0F}"),("ka","\u{E0100}\u{E0101}","か\u{E0100}\u{E0101}"),("ka","😀","か😀"),("ka","👩‍👩‍👧‍👦","か👩‍👩‍👧‍👦"),("ka","e\u{0301}","かe\u{0301}")] {
   check("event \(event.debugDescription)") { controller,client in
    type(controller,client,raw)
    precondition(!key(controller,client,event),"external event must pass through")
    equal(client.document,expected); equal(client.marked,"")
   }
  }
  for action in ["Enter","Space","Tab","focus"] {
   check("protected \(action)") { controller,client in
    type(controller,client,"@kuraido")
    switch action {
    case "Enter": _=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Space": _=key(controller,client," ",code:UInt16(kVK_Space))
    case "Tab": _=key(controller,client,"\t",code:UInt16(kVK_Tab))
    default: controller.commitComposition(client)
    }
    equal(client.document,"@kuraido"+(action=="Space" ? " " : ""));equal(client.marked,"")
   }
  }
  for action in ["Enter","Space","Tab","focus"] {
   check("mixed protected \(action)") { controller,client in
    type(controller,client,"kyouha「@kuraido」ashita")
    switch action {
    case "Enter": _=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Space": _=key(controller,client," ",code:UInt16(kVK_Space));_=key(controller,client,"\r",code:UInt16(kVK_Return))
    case "Tab": _=key(controller,client,"\t",code:UInt16(kVK_Tab))
    default: controller.commitComposition(client)
    }
    #if REAL_CONVERTER
    precondition(client.document.contains("「@kuraido」"),"protected token changed")
    precondition(!client.document.contains("kyouha") && !client.document.contains("ashita"),"Japanese gaps skipped")
    #else
    equal(client.document,"きょうは「@kuraido」あした")
    #endif
    equal(client.marked,"")
   }
  }
  check("Backspace at protected boundary") { controller,client in
   type(controller,client,"./z]");_=key(controller,client,"",code:UInt16(kVK_Delete));equal(client.marked,"./z")
   _=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"./z")
  }
  check("F6 override before marks") { controller,client in
   type(controller,client,"e");_=key(controller,client,"",code:UInt16(kVK_F6));_=key(controller,client,"\u{0301}\u{0300}");equal(client.document,"え\u{0301}\u{0300}")
  }
  check("F8 half-width katakana") { controller,client in
   // 実機では F8 の characters に U+F70B が入るが、main にはそれが変換中の文字に混ざる既知の不具合があり
   // (fix/mac-function-key-chars で修正中)、このテストでは characters を空にして送る。
   type(controller,client,"aiueo");precondition(key(controller,client,"",code:UInt16(kVK_F8)),"F8 must be consumed")
   equal(client.marked,"ｱｲｳｴｵ")
  }
  check("F9 override") { controller,client in
   type(controller,client,"@kuraido");_=key(controller,client,"",code:UInt16(kVK_F9));_=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"＠ｋｕｒａｉｄｏ")
  }
  for (name,scalar,code) in [("Left",UInt32(0xF702),kVK_LeftArrow),("Right",UInt32(0xF703),kVK_RightArrow),("Up",UInt32(0xF700),kVK_UpArrow),("Down",UInt32(0xF701),kVK_DownArrow),("ForwardDelete",UInt32(0xF728),kVK_ForwardDelete),("Home",UInt32(0xF729),kVK_Home),("End",UInt32(0xF72B),kVK_End),("PageUp",UInt32(0xF72C),kVK_PageUp),("PageDown",UInt32(0xF72D),kVK_PageDown),("F6",UInt32(0xF709),kVK_F6),("F8",UInt32(0xF70B),kVK_F8),("F13",UInt32(0xF710),kVK_F13)] {
   check("function key \(name) with private-use characters") { controller,client in
    type(controller,client,"aiueo")
    _=functionKey(controller,client,scalar,code:code)
    precondition(!hasFunctionKeyScalar(client.marked),"marked text has function key scalar: \(client.marked.debugDescription)")
    precondition(!hasFunctionKeyScalar(client.document),"document has function key scalar: \(client.document.debugDescription)")
    _=key(controller,client,"\r",code:UInt16(kVK_Return))
    precondition(!hasFunctionKeyScalar(client.document),"committed text has function key scalar: \(client.document.debugDescription)")
    equal(client.marked,"")
   }
  }
  check("Left arrow commits Latin text and passes through") { controller,client in
   type(controller,client,"hello")
   precondition(!functionKey(controller,client,0xF702,code:kVK_LeftArrow),"arrow must pass through to the app")
   equal(client.document,"hello");equal(client.marked,"")
  }
  check("Forward delete commits Latin text and passes through") { controller,client in
   type(controller,client,"hello")
   precondition(!functionKey(controller,client,0xF728,code:kVK_ForwardDelete),"delete must pass through to the app")
   equal(client.document,"hello");equal(client.marked,"")
  }
  check("F8 commits kana and passes through") { controller,client in
   type(controller,client,"aiueo")
   precondition(!functionKey(controller,client,0xF70B,code:kVK_F8),"unimplemented F8 must pass through to the app")
   equal(client.document,"あいうえお");equal(client.marked,"")
  }
  check("Option+Shift+K Apple logo stays a character") { controller,client in
   type(controller,client,"aiueo")
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[.option,.shift],timestamp:0,windowNumber:0,context:nil,characters:"\u{F8FF}",charactersIgnoringModifiers:"K",isARepeat:false,keyCode:UInt16(kVK_ANSI_K))!
   let consumed=controller.handle(event,client:client)
   if !consumed { client.insertText("\u{F8FF}",replacementRange:NSRange(location:NSNotFound,length:0)) }
   _=key(controller,client,"\r",code:UInt16(kVK_Return))
   precondition(client.document.unicodeScalars.contains { $0.value==0xF8FF },"U+F8FF must stay a character: \(client.document.debugDescription)")
   equal(client.marked,"")
  }
  check("JIS direct and kana modes") { controller,client in
   _=key(controller,client,"",code:UInt16(kVK_JIS_Eisu));type(controller,client,"ka");equal(client.document,"ka")
   _=key(controller,client,"",code:UInt16(kVK_JIS_Kana));type(controller,client,"ka");_=key(controller,client,"",code:UInt16(kVK_F6));_=key(controller,client,"\r",code:UInt16(kVK_Return));equal(client.document,"ka か")
  }
  // US / JIS 配列で英数・かなキーが無くても、Ctrl+Shift+J (日本語) / Ctrl+Shift+; ・ ' (英数) で切り替える。
  let ctrlShift:NSEvent.ModifierFlags=[.control,.shift]
  check("Ctrl+Shift+; switches to direct input") { controller,client in
   precondition(key(controller,client,";",code:UInt16(kVK_ANSI_Semicolon),flags:ctrlShift))
   type(controller,client,"ka");equal(client.document,"ka");equal(client.marked,"")
  }
  check("Ctrl+Shift+' switches to direct input") { controller,client in
   precondition(key(controller,client,"'",code:UInt16(kVK_ANSI_Quote),flags:ctrlShift))
   type(controller,client,"ka");equal(client.document,"ka");equal(client.marked,"")
  }
  check("Ctrl+Shift+J returns to Japanese") { controller,client in
   _=key(controller,client,"",code:UInt16(kVK_JIS_Eisu));type(controller,client,"ka");equal(client.document,"ka")
   precondition(key(controller,client,"j",code:UInt16(kVK_ANSI_J),flags:ctrlShift))
   type(controller,client,"ka");equal(client.marked,"か")
  }
  check("Ctrl+Shift+; commits composition first") { controller,client in
   type(controller,client,"@kuraido");precondition(client.marked != "")
   precondition(key(controller,client,";",code:UInt16(kVK_ANSI_Semicolon),flags:ctrlShift))
   equal(client.document,"@kuraido");equal(client.marked,"")
   type(controller,client,"ka");equal(client.document,"@kuraidoka")
  }
  check("Ctrl+Shift+' commits composition first") { controller,client in
   type(controller,client,"@kuraido");precondition(client.marked != "")
   precondition(key(controller,client,"'",code:UInt16(kVK_ANSI_Quote),flags:ctrlShift))
   equal(client.document,"@kuraido");equal(client.marked,"")
   type(controller,client,"ka");equal(client.document,"@kuraidoka")
  }
  check("Ctrl+Shift+; during composition does not pass the key to the app") { controller,client in
   type(controller,client,"@kuraido")
   precondition(key(controller,client,";",code:UInt16(kVK_ANSI_Semicolon),flags:ctrlShift))
   equal(client.document,"@kuraido")
   type(controller,client,"ka");equal(client.document,"@kuraidoka");equal(client.marked,"")
   precondition(!client.document.contains(";") && !client.document.contains("'"))
  }
  check("Ctrl+J and Cmd+Shift+J are not mode keys") { controller,client in
   _=key(controller,client,"",code:UInt16(kVK_JIS_Eisu));type(controller,client,"ka")
   // Shift なしの Ctrl+J・Command / Option 付きは対象外 (キーを使わず、直接入力のまま)。
   precondition(!key(controller,client,"j",code:UInt16(kVK_ANSI_J),flags:.control))
   precondition(!key(controller,client,"j",code:UInt16(kVK_ANSI_J),flags:[.command,.shift]))
   precondition(!key(controller,client,"j",code:UInt16(kVK_ANSI_J),flags:[.control,.shift,.command]))
   precondition(!key(controller,client,"j",code:UInt16(kVK_ANSI_J),flags:[.control,.shift,.option]))
   type(controller,client,"ka");equal(client.document,"kaka");equal(client.marked,"")
  }
  check("Command shortcut commits once") { controller,client in
   type(controller,client,"@kuraido");precondition(!key(controller,client,"a",flags:.command));equal(client.document,"@kuraido");equal(client.marked,"")
  }
  // 入力中の Ctrl キー (設定 ControlKeys)。設定は入力欄ごとの本体を作るときに読むので、controller を作る前に config.json を書き換える。
  // Control を押すと characters は制御文字になり (Ctrl+J → "\n")、Shift も無視される (US 配列の Ctrl+: は ";")。charactersIgnoringModifiers は Shift を含む (":")。
  func ctrl(_ controller:MeltypeInputController,_ client:EventClient,_ chars:String,_ ignoring:String,code:Int,shift:Bool=false)->Bool {
   let flags:NSEvent.ModifierFlags = shift ? [.control,.shift] : [.control]
   let event=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:flags,timestamp:0,windowNumber:0,context:nil,characters:chars,charactersIgnoringModifiers:ignoring,isARepeat:false,keyCode:UInt16(code))!
   return controller.handle(event,client:client)
  }
  guard let dataDirectory=ProcessInfo.processInfo.environment["MELTYPE_DATA_DIR"] else {fatalError("MELTYPE_DATA_DIR is required")}
  let configURL=URL(fileURLWithPath:dataDirectory).appendingPathComponent("config.json")
  let originalConfig=try? Data(contentsOf:configURL)
  func writeControlKeys(_ style:String?) {
   var object=(originalConfig.flatMap { try? JSONSerialization.jsonObject(with:$0) } as? [String:Any]) ?? [:]
   if let style { object["ControlKeys"]=style }
   try! JSONSerialization.data(withJSONObject:object).write(to:configURL)
  }
  writeControlKeys(nil)
  check("Ctrl+J without a setting is not assigned (ATOK style)") { controller,client in
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,"\n","j",code:kVK_ANSI_J),"Ctrl+J must pass to the app")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  writeControlKeys("Mac")
  check("Mac style Ctrl+J / Ctrl+K / Ctrl+L") { controller,client in
   type(controller,client,"aiueo")
   precondition(ctrl(controller,client,"\u{0B}","k",code:kVK_ANSI_K),"Ctrl+K must be consumed"); equal(client.marked,"アイウエオ")
   precondition(ctrl(controller,client,"\u{0C}","l",code:kVK_ANSI_L),"Ctrl+L must be consumed"); equal(client.marked,"ａｉｕｅｏ")
   precondition(ctrl(controller,client,"\n","j",code:kVK_ANSI_J),"Ctrl+J must be consumed"); equal(client.marked,"あいうえお")
   equal(client.document,"")
  }
  check("Mac style Ctrl+; half-width katakana") { controller,client in
   type(controller,client,"aiueo")
   precondition(ctrl(controller,client,";",";",code:kVK_ANSI_Semicolon),"Ctrl+; must be consumed"); equal(client.marked,"ｱｲｳｴｵ")
   equal(client.document,"")
  }
  check("Mac style Ctrl+: and Ctrl+' half-width alphanumerics") { controller,client in
   type(controller,client,"aiueo")
   _=ctrl(controller,client,"\n","j",code:kVK_ANSI_J)
   // US 配列の Ctrl+: は Ctrl+Shift+; (characters は Shift を無視して ";" になる)
   precondition(ctrl(controller,client,";",":",code:kVK_ANSI_Semicolon,shift:true),"Ctrl+: must be consumed"); equal(client.marked,"aiueo")
   _=ctrl(controller,client,"\n","j",code:kVK_ANSI_J); equal(client.marked,"あいうえお")
   precondition(ctrl(controller,client,"'","'",code:kVK_ANSI_Quote),"Ctrl+' must be consumed"); equal(client.marked,"aiueo")
   equal(client.document,"")
  }
  check("Mac style Ctrl+U is not assigned and Ctrl+N passes when empty") { controller,client in
   precondition(!ctrl(controller,client,"\u{0E}","n",code:kVK_ANSI_N),"Ctrl+N with no input must pass to the app")
   type(controller,client,"aiueo")
   precondition(!ctrl(controller,client,"\u{15}","u",code:kVK_ANSI_U),"Ctrl+U must pass to the app")
   equal(client.document,"あいうえお"); equal(client.marked,"")
  }
  if let originalConfig { try! originalConfig.write(to:configURL) } else { try? FileManager.default.removeItem(at:configURL) }

  #if !REAL_CONVERTER
  // PageDown / PageUp / Shift+↓↑ のページ送りで、候補ウィンドウの選択が本体の選択からずれないこと (9 個以上の移動、最後⇔最初の回り込み)。
  // NSEvent の characters は空にして送る: main には PageUp/PageDown の U+F72C/F72D が文字として混ざる不具合があり、別ブランチ (fix/mac-function-key-chars) で直している。
  check("candidate paging keeps window selection in sync") { controller,client in
   // 変換エンジンの関数を登録するのは main.swift なので、ここでも登録する (他の確認に影響しないよう、このテストを最後に置く)。
   NativeCore.shared.initialize()
   let window=RecordingCandidates(server:server,panelType:kIMKSingleColumnScrollingCandidatePanel)!
   candidatesWindow=window
   defer {candidatesWindow=nil}
   type(controller,client,"tsukue")
   _=key(controller,client," ",code:UInt16(kVK_Space))
   // 変換エンジンの候補は最初の候補送りで足されるので、↓ ↑ で先頭に戻しておく。
   _=key(controller,client,"",code:UInt16(kVK_DownArrow)); _=key(controller,client,"",code:UInt16(kVK_UpArrow))
   let list=(controller.candidates(nil) as? [String]) ?? []
   precondition(list.count>18,"need 3+ pages: \(list)")
   precondition(window.shown && window.position==0,"window starts at head")
   let size=9; let pages=(list.count+size-1)/size
   var page=0
   for step in ["PageDown","PageDown","PageUp","shiftDown","shiftUp","PageUp"]+Array(repeating:"PageUp",count:1)+Array(repeating:"PageDown",count:pages) {
    switch step {
    case "PageDown": _=key(controller,client,"",code:UInt16(kVK_PageDown)); page=(page+1)%pages
    case "PageUp": _=key(controller,client,"",code:UInt16(kVK_PageUp)); page=(page+pages-1)%pages
    case "shiftDown": _=key(controller,client,"",code:UInt16(kVK_DownArrow),flags:.shift); page=(page+1)%pages
    default: _=key(controller,client,"",code:UInt16(kVK_UpArrow),flags:.shift); page=(page+pages-1)%pages
    }
    precondition(window.position==page*size,"\(step): window at \(window.position), expected \(page*size)")
   }
   // 本体の選択もウィンドウと同じ位置か: 数字キーの 1 で、そのページの先頭の候補が確定する。
   _=key(controller,client,"",code:UInt16(kVK_PageDown)); page=(page+1)%pages
   _=key(controller,client,"1",code:UInt16(kVK_ANSI_1))
   equal(client.document,list[page*size])
  }
  #endif
  #if REAL_CONVERTER
  let converter=MeltypeConverter.shared
  let candidates=converter.candidates(for:"きょう")
  precondition(candidates.contains("今日"),"real dictionary conversion missing: \(candidates)")
  let clauses=converter.clauses(for:"きょうはいいてんき",context:nil)
  precondition(clauses.map(\.reading).joined()=="きょうはいいてんき","real clause readings lost")
  precondition(clauses.map(\.text).joined().contains("今日"),"real clause conversion missing")
  guard let directory=NativeCore.shared.dataDirectory else {fatalError("missing data directory")}
  precondition(FileManager.default.fileExists(atPath:directory+"/azooKey"),"converter ignored isolated data directory")
  passed+=3
  print("PASS: actual azooKey candidates, clause reading coverage, isolated data directory")
  #endif
  print("\(passed) Mac event/integration cases passed (synthetic client; OS IME registration and app GUI NOT_RUN)")
 }
}
