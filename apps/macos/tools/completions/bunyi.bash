# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

_bunyi_complete() {
  local current previous
  current="${COMP_WORDS[COMP_CWORD]}"
  previous="${COMP_WORDS[COMP_CWORD-1]}"
  case "$previous" in
    --mode) COMPREPLY=( $(compgen -W 'preset design clone' -- "$current") ); return ;;
    --language) COMPREPLY=( $(compgen -W 'auto english chinese japanese korean german french russian portuguese spanish italian' -- "$current") ); return ;;
  esac
  if [ "$COMP_CWORD" -eq 1 ]; then
    COMPREPLY=( $(compgen -W 'generate models speakers transcribe play voices history doctor backup config logs server jobs version' -- "$current") )
  else
    COMPREPLY=( $(compgen -W 'preset design clone list status download verify remove add show create restore get set path tail clear run start preload unload stop follow cancel --json --jsonl --one-shot --require-server --detach --help' -- "$current") )
  fi
}
complete -F _bunyi_complete bunyi
