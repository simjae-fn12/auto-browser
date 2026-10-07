# Ego Windows

Windows용 브라우저의 현재 구현은 [`native/`](./native/README.md)에 있습니다. Windows Forms 창과 Microsoft Edge WebView2 엔진을 사용하며 Electron은 필요하지 않습니다. 사람 탭과 에이전트 탭은 같은 영구 프로필을 공유합니다.

```powershell
cd .\native
dotnet publish .\native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
.\publish\EgoWindowsNative.exe
.\cli.ps1 list
```

빌드에는 .NET 10 SDK가 필요하고, 실행에는 Microsoft Edge WebView2 런타임이 필요합니다. JavaScript 자동화 스크립트에는 Node.js가 필요합니다.

AI 에이전트가 여러 동작을 한 번에 수행할 때는 `native/agent.mjs`에 JavaScript를 전달합니다. 각 작업은 이름이 붙은 Space에서 독립적인 탭·커서·스크롤 상태를 유지합니다. [사용 예시](./native/README.md#에이전트-cli)를 참고하세요.

`publish` 폴더의 실행 파일과 `WebView2Loader.dll`, `runtimes` 폴더를 함께 보관하세요. 실행과 빌드, 에이전트 명령은 [`native/README.md`](./native/README.md)를 참고하세요.

루트의 `main.cjs`, `chrome.*`, `preload.cjs`, `cli.mjs`는 이전 Electron 프로토타입입니다.
