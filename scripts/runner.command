#!/bin/zsh -l
# Link para Mac — executor de tarefas.
# Fica rodando nesta janela do Terminal e executa, em ordem, os arquivos .sh colocados em
# link-para-mac/.runner/queue/. Saída de cada tarefa vai para link-para-mac/logs/<nome>.log.
# Para parar: feche esta janela, aperte Ctrl-C, ou crie o arquivo link-para-mac/.runner/STOP.
set -u
SCRIPT_DIR="${0:A:h}"
ROOT="${SCRIPT_DIR:h}"
RUN="$ROOT/.runner"
mkdir -p "$RUN/queue" "$RUN/running" "$RUN/done" "$ROOT/logs"
rm -f "$RUN/STOP"
print "Link para Mac — runner iniciado em $(date)"
print "Fila: $RUN/queue    Logs: $ROOT/logs"
print "Para parar: feche a janela, Ctrl-C, ou crie $RUN/STOP"
print "runner-ready $(date +%s) pid=$$" > "$RUN/STATUS"
while true; do
  if [[ -f "$RUN/STOP" ]]; then
    print "STOP encontrado. Saindo."; rm -f "$RUN/STOP"
    print "runner-stopped $(date +%s)" > "$RUN/STATUS"; exit 0
  fi
  job="$(print -rl -- "$RUN/queue"/*.sh(N) | sort | head -1)"
  if [[ -n "$job" ]]; then
    name="${job:t:r}"
    mv "$job" "$RUN/running/$name.sh"
    log="$ROOT/logs/$name.log"
    print; print "▶ $name  ($(date +%H:%M:%S))"
    print "running $name $(date +%s)" > "$RUN/STATUS"
    zsh "$RUN/running/$name.sh" </dev/null 2>&1 | tee "$log"
    code=${pipestatus[1]}
    print "$code" > "$RUN/done/$name.exit"
    mv "$RUN/running/$name.sh" "$RUN/done/$name.sh"
    print "■ $name terminou com código $code  ($(date +%H:%M:%S))"
    print "idle $(date +%s)" > "$RUN/STATUS"
  fi
  sleep 1
done
