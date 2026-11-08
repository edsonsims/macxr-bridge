// SPDX-License-Identifier: MIT
// Link para Mac: pontos de encaixe pro código do app. O Link é genérico (pacote com.linkparamac.link); o que é do
// app — ligar a detecção real dele, onde a etiqueta pousou, o que o raycast de reserva deve ignorar — chega por uma
// ponte pequena do próprio projeto (no Hello World: Assets/_Project/LinkParaMac/HelloWorldLinkHooks.cs).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class LinkHooks
    {
        /// <summary>O app colocou algo no mundo (rótulo, posição). Usado pelos testes de sessão e pela depuração.</summary>
        public static event Action<string, Vector3> Placed;
        /// <summary>Um lote de colocações (uma resposta da detecção).</summary>
        public static event Action<IReadOnlyList<(string label, Vector3 position)>> PlacedBatch;

        public static void ReportPlaced(string label, Vector3 position) => Placed?.Invoke(label, position);
        public static void ReportPlacedBatch(IReadOnlyList<(string label, Vector3 position)> batch)
        {
            PlacedBatch?.Invoke(batch);
            foreach (var p in batch) Placed?.Invoke(p.label, p.position);
        }

        /// <summary>Liga a detecção REAL do app quando a câmera do Quest chega (desliga mocks, inscreve a fonte…).
        /// Devolve um relatório curto.</summary>
        public static Func<string> WireDetection;
        /// <summary>Desfaz o WireDetection (fim do feed / do Play).</summary>
        public static Action UnwireDetection;
        /// <summary>Limiar de confiança do próprio app (o do agent/provider o Link já ajusta).</summary>
        public static Action<float> ApplyAppThreshold;
        /// <summary>Colisores que o raycast de reserva deve ignorar (etiquetas do app, sala mock…).</summary>
        public static Func<Collider, bool> IgnoreCollider;
        /// <summary>Diagnóstico: pausa/retoma a fonte de detecção do app.</summary>
        public static Action<bool> PauseAppDetection;
        /// <summary>Uma linha extra no detect-status sobre a fonte do app.</summary>
        public static Func<string> AppStatus;

        // Testes em passo travado: o teste decide quando o app pode começar a próxima detecção.
        /// <summary>Comece uma detecção agora (no quadro atual).</summary>
        public static Action DetectNow;
        /// <summary>Não comece outra até o próximo DetectNow.</summary>
        public static Action HoldDetection;
        /// <summary>True se uma detecção começou depois do último DetectNow.</summary>
        public static Func<bool> DetectionStarted;
    }
}
