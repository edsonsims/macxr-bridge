# Link para Mac — pesquisa e plano (7/nov/2026)

Objetivo (Edson): um "Link" para macOS que funcione como o Meta Quest Link no Windows: Play no Unity Editor → o app aparece no Quest 3 com tracking, mãos e passthrough reais.
Decisão (7/nov): **fork próprio**, em cima do OXRSys. Nome "Link para Mac" é provisório ("Link" é marca da Meta; escolher um nome neutro antes de publicar).

## Fatos
- A Meta não oferece Link no macOS (doc Unity Link, abr/2026: "Link is currently only supported on Windows"). Parallels/Windows ARM não roda o Link; Boot Camp não existe em Apple Silicon.
- **OXRSys** (ex-OpenXR OSX), de Yannick Comte (demonixis): https://github.com/demonixis/oxrsys
  - MPL-2.0; 114 estrelas, 10 contribuidores; 1.3.0 lançada em 4/nov/2026.
  - Runtime OpenXR nativo para macOS em C++20; Metal (`XR_KHR_metal_enable`) e Vulkan via MoltenVK; encoder VideoToolbox H.264/H.265 (HEVC Main10 negociado).
  - Transporte: USB via `adb reverse` (portas 9944/9945/9946; 9948 "spatial" reservada e não implementada) ou Wi-Fi UDP; 1.3.0 adiciona USB CDC-NCM.
  - Cliente Quest/Pico em Android (MediaCodec, decode ~11 ms em H.264); cliente Vision Pro; simulador macOS; app "Home" em SwiftUI (seleção de runtime, ADB, diagnóstico). Cliente se instala por sideload (`./gradlew assembleDebug` + `adb install`).
  - Seleção de runtime: `oxrsys-runtime.json` via `XR_RUNTIME_JSON` ou `~/.config/openxr/1/active_runtime.json`; config em `~/Library/Application Support/OXRSys/oxrsys-runtime.toml`.
  - Já tem: mãos (`XR_EXT_hand_tracking` vindo do cliente Quest), passthrough (`XR_FB_passthrough` no cliente; alfa real empacotado abaixo dos olhos, 1.3.0), foveation, quad/cylinder layers compostas antes do encode, pacote Unity `net.demonixis.oxrsys-unity` (seleção de runtime no Editor).
  - Não tem: cena/âncoras (`XR_FB_scene`, `XR_FB_spatial_entity`), câmera do Quest (PCA), microgestos, áudio (só esqueleto), validação com o Meta XR SDK (OVRPlugin) no Mac.
- MRUK tem `SaveSceneToJsonString()` e `LoadSceneFromJsonString()` (fonte de dados `Json`), o que permite usar a sala real no Editor sem suporte do runtime.
- Meta XR Simulator Data Forwarding só encaminha controles Touch (não mãos nem cabeça).

## O que o Hello World precisa do Link
| Precisa | OXRSys hoje | Na competição |
|---|---|---|
| Play Mode → headset, head tracking | sim | usar |
| Mãos e pinça (ISDK) | sim | usar |
| Passthrough com etiquetas por cima | sim (validar com `OVRPassthroughLayer`) | validar no dia 1 |
| Câmera do Quest (PCA) para detectar objetos | não | Editor: webcam do Mac ou quadros gravados; real só no build |
| Sala (MRUK) | não | exportar JSON no headset 1x, carregar no Editor |
| Profundidade (`EnvironmentRaycastManager`) | não | raycast nos planos do MRUK no Editor |
| Microgestos | não | teclado no Editor; real no build |
| Âncoras persistentes | não | só no build |
| Microfone/áudio | não | mic e alto-falantes do Mac no Editor |
| Meta XR SDK 207 (OVRPlugin) no Mac | não testado | **risco nº 1**: testar no dia 1 |

## Plano
- **Fase 1 (1–2 dias, antes de 18/nov):** fork; build do runtime, Home e cliente Quest; Unity 6 + Meta XR SDK 207 em Play Mode no Quest 3 (cabeça, mãos, passthrough). Ferramenta de exportar/importar a sala (MRUK JSON). Fonte de câmera trocável no Editor. Stubs de `XR_FB_passthrough` se o OVRPlugin exigir.
- **Fase 2 (depois de 18/nov):** relay da câmera do Quest (Camera2 → H.264 → Mac → câmera virtual CoreMediaIO + intrínsecos/pose), relay de cena e âncoras pelo canal 9948, microgestos, áudio do headset, instalador de um clique, docs.

## Estrutura criada em 7/nov (pasta raiz `/Users/edsonhenrique/Documents/GitHub/meta-vr`)
- `docs/` — espelho destes docs do projeto.
- `hello-world/` — projeto Unity do app (vazio ainda).
- `link-para-mac/oxrsys/` — clone do OXRSys (main em `a87f56f`, depois da tag 1.3.0), remote `upstream`, branch `link-para-mac`.
- `link-para-mac/scripts/` — `00-check-prereqs.sh`, `10-build-runtime.sh`, `20-build-home.sh`, `30-build-quest-client.sh`, `40-install-quest.sh`, `50-configure-runtime.sh`, `60-unity-add-package.sh`, `70-pull-room-json.sh`, `all.sh`. Sintaxe verificada com `zsh -n`; ainda não executados no Mac.
- `link-para-mac/unity-tools/RoomJsonExporter.cs` — exporta a sala real do MRUK para JSON no headset.
- `.gitignore` + `git init` (sem commits). `link-para-mac/oxrsys/` está ignorado por ser repo próprio.
- Config que o `50` grava no toml: transport auto (USB primeiro), h265, 80 Mbps, 72 Hz, `passthrough_enabled = true`, `app_alpha_blend_passthrough = true`.

## Progresso 7/nov (noite) — Fase 1 executada no Mac
Execução pelo `scripts/runner.command` (executor que roda a fila `.runner/queue/` numa janela do Terminal; logs em `link-para-mac/logs/`).
- Mac: macOS 27.2, Xcode 27.0, cmake 3.30.5, Swift 6.4, Homebrew; Android SDK em `~/Library/Android/sdk`; Java só 21 (Homebrew), sem 17. **Unity não está instalado** (nada em /Applications/Unity) — instalar Unity Hub + 6000.0.66f2+ com Android Build Support antes de começar o app.
- Instalado: ninja 1.13.2, vulkan-headers 1.4.363, platform android-35, NDK 28.2.13676358.
- **Runtime compilado** (21 s): `oxrsys/build/macos-arm64/runtime/liboxrsys-runtime.dylib` + `oxrsys-runtime.json` (arm64).
- **OXRSys Home compilado** (13 s): `oxrsys/build/home/Build/Products/Debug/OXRSys Home.app`.
- **APK do cliente compilado** (47 s, Gradle 9.4 com Java 21 funcionou): `oxrsys/clients/android-vr/app/build/outputs/apk/release/app-release.apk` (pacote `net.demonixis.oxrsys.android`, app "OXRSys").
- **Runtime registrado** como OpenXR padrão do usuário: `~/.config/openxr/1/active_runtime.json` → manifest; LaunchAgent `net.demonixis.oxrsys.runtime-env` define `XR_RUNTIME_JSON` para apps GUI. toml em `~/Library/Application Support/OXRSys/oxrsys-runtime.toml`: transport auto, h265, 80 Mbps, 72 Hz, passthrough_enabled e app_alpha_blend_passthrough = true. Desfazer: `oxrsys/scripts/oxrsys_runtime_default.sh unset`.
- **Pendente:** instalar o APK no Quest 3 (`scripts/40-install-quest.sh`; o headset não estava ligado por USB). Depois: Unity + checklist do dia 1.

## 8/nov (madrugada) — RISCO Nº 1 RESOLVIDO: Meta XR SDK 207 roda no runtime do Mac e o Quest 3 recebe o Play Mode
Ferramentas criadas para testar sem tocar na tela: `hello-world/unity/HelloWorld/Assets/_Project/LinkParaMac/Editor/LinkBridge.cs` (ponte de comandos do Editor: `.link/cmd/*.cmd` → `.link/out/*.out`, Console espelhado em `.link/unity.log`, `status.json`, comandos `play/stop/refresh/menu/runtime/xr-standalone/hierarchy/sh/shbg`) e o `scripts/runner.command` (fila de shell no Terminal). Regra aprendida: **nunca rodar adb pela ponte do Unity** (bloqueia o Editor); adb só pelo runner.

Correções no fork (branch `link-para-mac`, ainda sem commit), em ordem de descoberta:
1. `EntryPoint.cpp`: aceitar qualquer patch da API 1.1 (o OVRPlugin pede 1.1.61; o runtime era compilado com 1.1.57 e recusava).
2. `EntryPoint.cpp`/`Instance.cpp`: anunciar e preencher `XR_META_headset_id` (o `MetaXRFeature` só liga em runtime que expõe essa extensão).
3. `Instance.cpp`: `runtimeName = "OXRSys Runtime for Meta Quest"` e `systemName = "Meta Quest 3"` (o OVRPlugin recusa "Non-Oculus OpenXR runtime" pelo nome).
4. `Session.cpp`: `xrGetReferenceSpaceBoundsRect(STAGE)` devolve 3×3 m quando o guardian ainda não chegou (em vez de `XR_SPACE_BOUNDS_UNAVAILABLE`).
5. `EntryPoint.cpp`: anunciar `XR_EXT_local_floor` (além do LOCAL_FLOOR do core 1.1).
6. **A correção decisiva** — `InputManager.cpp`: antes do primeiro head pose, LOCAL ficava no chão (0,0,0), igual ao LOCAL_FLOOR; o Unity lia LOCAL_FLOOR − LOCAL = 0, tratava como "piso desconhecido" e recriava o app space **todo frame** (`OpenXRSession::Tick → HandleSetTrackingOriginMode → CreateLocalFloorSpaceEx`), e o `xrEndFrame` do OVRPlugin apontava para o espaço destruído (`XR_ERROR_HANDLE_INVALID`, zero frames). Agora LOCAL padrão fica na altura dos olhos (0,1.6,0).
7. Diagnóstico temporário ainda no código: backtrace em `CreateReferenceSpace`, `TRACE` de chamadas, avisos em `EndFrame`. Remover antes de publicar.

Resultado: `ovrPluginInitialized = True`, sessão focada, `VideoEncoder: submit stereo frame srcL=1512x1680 srcR=1512x1680`, cliente Quest 3 conectado (USB adb ou Wi-Fi), tracking e mãos chegando (`left hand active flags=0x71`), WaitFrame estável em 13.9 ms (72 Hz). Pendentes conhecidos: passthrough (`Failed to initialize Insight Passthrough`, falta `XR_FB_passthrough`), `EnvironmentRaycastManager` não suportado, microgestos, âncoras, PCA.

8. `HandTracker.cpp`/`EntryPoint.cpp`: anunciar `XR_FB_hand_tracking_aim` e `XR_FB_hand_tracking_mesh`; preencher `XrHandTrackingScaleFB` (scale 1.0) e `XrHandTrackingAimStateFB` (pinça por distância polegar–ponta, aim do ombro aproximado pela palma); `xrGetHandMeshFB` mínimo (26 juntas, 0 vértices). Motivo: o ISDK (`FromOVRHandDataSource`) só considera a mão conectada com `HandScale > 0`; sem isso `HandVisual.IsVisible` ficava falso e nada aparecia. Resultado medido: `Hand.IsConnected = true`, `IsVisible = true`, `IsPointerPoseValid = true`.
9. App (`hello-world`): `EditorKeyboardDriver` erguia o rig para 1,6 m e inclinava 35° porque checava `XRSettings.isDeviceActive` no `Start`, antes do XR iniciar; agora espera até 2 s e desiste se o XR estiver ativo. Era a "cena no chão" (câmera em 2,57 m olhando para baixo). Medido depois: TrackingSpace (0,0,0), CenterEyeAnchor y = 1,19 m.

Observações de operação: `Microphone.Start` do app trava o Editor até o macOS mostrar/receber a permissão de microfone para o Unity (uma vez). A autorização USB (adb) do Quest cai quando o servidor adb reinicia; o cliente cai para Wi-Fi (ABR em 10 Mbps, decode ~12 ms, idade do frame ~20 ms) — reautorizar no headset e refazer `adb reverse` para USB.

Projeto Unity: XR Plug-in Management do Standalone configurado via `xr-standalone` (OpenXR loader + Meta XR Feature + Touch/Touch Plus/Hand Interaction; `runInBackground = true`). Unity registrado no Hub.

## Fontes
- Unity Link: https://developers.meta.com/vr/documentation/unity/unity-link/
- OXRSys: https://github.com/demonixis/oxrsys (releases, docs/architecture.md, docs/quest.md, docs/support-matrix.md)
- Artigo (7/mai/2026): https://skarredghost.com/2026/05/07/openxr-osx-vr-mac/
- Data Forwarding: https://developers.meta.com/horizon/documentation/unity/xrsim-data-forwarding/
- MRUK v76 API: https://developers.meta.com/horizon/reference/mruk/v76/class_meta_x_r_m_r_utility_kit_m_r_u_k
- PCA: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
