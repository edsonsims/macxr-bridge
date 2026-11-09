#!/bin/zsh -l
# MacXR Bridge — task runner.
# Keeps running in this Terminal window and runs, in order, the .sh files dropped into
# macxr-bridge/.runner/queue/. Each task's output goes to macxr-bridge/logs/<name>.log.
# To stop: close this window, press Ctrl-C, or create the file macxr-bridge/.runner/STOP.
set -u
SCRIPT_DIR="${0:A:h}"
ROOT="${SCRIPT_DIR:h}"
RUN="$ROOT/.runner"
mkdir -p "$RUN/queue" "$RUN/running" "$RUN/done" "$ROOT/logs"
rm -f "$RUN/STOP"
print "MacXR Bridge — runner started at $(date)"
print "Queue: $RUN/queue    Logs: $ROOT/logs"
print "To stop: close the window, press Ctrl-C, or create $RUN/STOP"
print "runner-ready $(date +%s) pid=$$" > "$RUN/STATUS"
while true; do
  if [[ -f "$RUN/STOP" ]]; then
    print "STOP found. Exiting."; rm -f "$RUN/STOP"
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
    print "■ $name finished with exit code $code  ($(date +%H:%M:%S))"
    print "idle $(date +%s)" > "$RUN/STATUS"
  fi
  sleep 1
done
