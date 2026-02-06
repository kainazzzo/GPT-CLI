# bash completion for deploy.sh
_gptcli_deploy_complete() {
  local cur opts
  COMPREPLY=()
  cur="${COMP_WORDS[COMP_CWORD]}"
  opts="build up restart logs stop completion"
  if [[ ${COMP_CWORD} -eq 1 ]]; then
    COMPREPLY=( $(compgen -W "${opts}" -- "${cur}") )
  fi
  return 0
}

complete -F _gptcli_deploy_complete deploy.sh ./deploy.sh ./deploy/deploy.sh deploy deploy/deploy.sh
