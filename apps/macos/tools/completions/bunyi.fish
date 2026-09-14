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

complete -c bunyi -f
complete -c bunyi -l json -d 'Write one JSON result'
complete -c bunyi -l jsonl -d 'Write JSON-lines progress and a terminal result'
complete -c bunyi -l one-shot -d 'Bypass a running server'
complete -c bunyi -l require-server -d 'Require a running server'
complete -c bunyi -l help -d 'Show help'
complete -c bunyi -n '__fish_use_subcommand' -a 'generate models speakers transcribe play voices history doctor backup config logs server jobs version'
complete -c bunyi -n '__fish_seen_subcommand_from generate' -a 'preset design clone'
complete -c bunyi -n '__fish_seen_subcommand_from models' -a 'list status download verify remove'
complete -c bunyi -n '__fish_seen_subcommand_from server' -a 'run start status preload unload stop'
complete -c bunyi -n '__fish_seen_subcommand_from voices' -a 'list add remove'
complete -c bunyi -n '__fish_seen_subcommand_from history' -a 'list show remove'
complete -c bunyi -n '__fish_seen_subcommand_from backup' -a 'create restore'
complete -c bunyi -n '__fish_seen_subcommand_from config' -a 'list get set'
complete -c bunyi -n '__fish_seen_subcommand_from logs' -a 'path tail clear'
complete -c bunyi -n '__fish_seen_subcommand_from jobs' -a 'status follow cancel'
