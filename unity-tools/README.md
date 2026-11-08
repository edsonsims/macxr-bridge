# unity-tools

Scripts Unity do Link para Mac. Copie para `Assets/LinkParaMac/` do projeto.

- `RoomJsonExporter.cs` — no headset, salva a sala real do MRUK em `room.json` (persistentDataPath).
  Puxe com `scripts/70-pull-room-json.sh <package name>` e, no Editor, configure o componente MRUK:
  Scene Settings → Data Source = **Device With Json Fallback** → Scene Jsons = `room.json`.
  Assim, no Mac (onde o runtime ainda não entrega a cena), o Editor usa a sua sala de verdade.

Planejado (Fase 1):
- `PassthroughCameraSource` — fonte de quadros trocável para o Editor (webcam do Mac ou quadros gravados no Quest),
  para o pipeline de detecção de objetos rodar no Mac até o relay da câmera existir.
