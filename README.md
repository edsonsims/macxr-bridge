# Link para Mac

Um "Meta Quest Link" para macOS: aperte Play no Unity no Mac e o app aparece no Quest 3 com
cabeça, mãos e passthrough de verdade. Fork do [OXRSys](https://github.com/demonixis/oxrsys)
(runtime OpenXR nativo para macOS, MPL-2.0) com o que falta para desenvolver em realidade mista
com o Meta XR SDK. Nome provisório: "Link" é marca da Meta; escolher um nome neutro antes de publicar.

Pesquisa, decisões e backlog: [`../docs/link-para-mac.md`](../docs/link-para-mac.md).

## Como funciona

```
Unity Editor (Mac) ──OpenXR──▶ runtime OXRSys ──VideoToolbox HEVC──▶ USB (adb reverse) ou Wi-Fi ──▶ cliente no Quest
                                      ◀────────── cabeça, mãos (XR_EXT_hand_tracking), controles ◀──────────
```

- `oxrsys/` — o fork (branch `link-para-mac`; remote `upstream` = projeto original).
- `scripts/` — passo a passo automatizado da Fase 1 (abaixo).
- `unity-tools/` — scripts Unity: exportar a sala real do headset para usar no Editor.

## Fase 1 — fazer funcionar (antes de 18/nov)

Pré-requisitos no Mac: macOS 14+, Xcode (+ Metal Toolchain), `brew install cmake ninja vulkan-headers`,
Java 17, Android SDK (platform 35, NDK 28.2.13676358, cmake 3.22.1, platform-tools), Unity 6000.0.66f2+.
Quest 3 em modo desenvolvedor, com rastreamento de mãos ligado.

```bash
cd link-para-mac/scripts
./00-check-prereqs.sh       # confere tudo e diz o que falta
./all.sh                    # ou um por vez:
./10-build-runtime.sh       #   runtime (build/macos-arm64/runtime/oxrsys-runtime.json)
./20-build-home.sh          #   app OXRSys Home (status, ADB, configurações)
./30-build-quest-client.sh  #   APK do cliente (assembleRelease)
./40-install-quest.sh       #   instala no Quest + adb reverse 9944/9945/9946/9948
./50-configure-runtime.sh   #   runtime padrão do usuário + toml com passthrough e alfa ligados
```

Depois, no projeto Unity (Unity 6 + OpenXR Plugin + Meta XR All-in-One 207):

```bash
./60-unity-add-package.sh /caminho/do/ProjetoUnity   # adiciona o pacote net.demonixis.oxrsys-unity
```

No Unity: `Tools → OpenXR → Use OXRSys Runtime` → abra o cliente no Quest → **Play**.
Observação: o Unity precisa ter sido aberto *depois* do `50-configure-runtime.sh` para herdar o `XR_RUNTIME_JSON`.

### Checklist de validação (dia 1)
1. Cena vazia com `OVRCameraRig`: Play mostra a cena no Quest e a cabeça move a câmera.
2. Mãos: `OVRHand` / ISDK recebem juntas; a pinça funciona.
3. Passthrough: `OVRPassthroughLayer` ou câmera com fundo transparente aparece como passthrough no Quest.
   Se o OVRPlugin reclamar de `XR_FB_passthrough` ausente, este é o primeiro item do fork (ver backlog).
4. Latência aceitável para iterar (mexer a mão e ver a resposta sem enjoo).
5. Sala real no Editor: `unity-tools/RoomJsonExporter.cs` no build → `./70-pull-room-json.sh <package>` → MRUK com Data Source = Device With Json Fallback.

### O que ainda NÃO vem pelo Link (use no build do headset)
Câmera do Quest (PCA), âncoras espaciais, cena/âncoras do MRUK vindas do headset (use o JSON), microgestos, áudio do headset.

## Backlog do fork (Fase 2, depois de 18/nov)
1. `XR_FB_passthrough` (e `XR_FB_composition_layer_alpha_blend`) expostas ao app, mapeadas para o alfa que o runtime já transmite — para o `OVRPassthroughLayer` funcionar sem mudanças no app.
2. Relay da câmera do Quest: Camera2 no cliente → H.264 → Mac → câmera virtual (CoreMediaIO Camera Extension) com intrínsecos e pose da câmera, para o `PassthroughCameraAccess` funcionar no Editor.
3. Relay de cena e âncoras (`XR_FB_scene`, `XR_FB_spatial_entity`) pelo canal "spatial" 9948, já reservado no protocolo.
4. Microgestos (`XR_META_hand_tracking_microgestures`), áudio do headset (microfone e alto-falante), instalador de um clique, documentação.

## Licença
O código herdado do OXRSys continua MPL-2.0 (arquivos modificados ficam MPL-2.0). Código novo fora de `oxrsys/`: a definir.
