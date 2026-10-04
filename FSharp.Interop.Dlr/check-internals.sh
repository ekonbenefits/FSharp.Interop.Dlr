#!/usr/bin/env bash
# Patterns fixed more than once, kept fixed (#163): each idiom has one home, and a second copy fails
# the gate. Run from anywhere: ./FSharp.Interop.Dlr/check-internals.sh (exit 1 on any violation).
set -uo pipefail
cd "$(dirname "$0")"
sources=(*.fs)
failed=0

# Lines matching $2 in the sources, other than those matching $3 (the idiom's one home).
violations() {
  grep -nE "$1" "${sources[@]}" | grep -vE "$2" || true
}

# Every line matching $1, when there is more than its one home.
more_than_once() {
  local found
  found="$(grep -nE "$1" "${sources[@]}" || true)"
  if [[ $(echo -n "$found" | grep -c .) -gt 1 ]]; then echo "$found"; fi
}

check() {
  local what="$1" found="$2"
  if [[ -n "$found" ]]; then
    echo "check-internals: $what"
    echo "$found" | sed 's/^/  /'
    failed=1
  fi
}

# A user's delegate type's Invoke and constructor go through DelegateMembers (#121, #150): F#
# compiles an internal delegate's members internal, and a public-only lookup returns null. Site
# and per-key delegates (always public) are named `siteDelegate`, so they do not match.
check "a delegate's Invoke/constructor looked up outside DelegateMembers (use DelegateMembers.invokeOf/constructorOf)" \
  "$(violations '\b(delegateType|dt)\.(GetMethod\("Invoke"\)|GetConstructor\()' 'GetConstructor\(flags, null')"

# One generic quotation walk fallthrough: Quotation.rebuild.
check "a hand-rolled quotation rebuild (use Quotation.rebuild)" \
  "$(violations 'RebuildShapeCombination' 'ExprShape\.RebuildShapeCombination\(shape, List\.map f args\)')"

# One DynamicMethod policy: Emit.factory.
check "a DynamicMethod outside Emit.factory" \
  "$(violations 'DynamicMethod\(' 'DynamicMethod\(name, returnType, parameters, owner, true\)')"

# One place a `unit -> R` becomes a parameterless signature: FunctionShapes.parameters.
check "a hand-rolled unit-domain strip (use FunctionShapes.parameters)" \
  "$(violations 'if ds = \[ typeof<unit> \] then \[\] else ds' 'domains funcType \|> Option\.map')"

# One place a MethodInfo is taken from a quotation: Quotation.methodOf.
check "a MethodInfo matched out of a quotation by hand (use Quotation.methodOf)" \
  "$(violations 'Patterns\.Call\(_, mi, _\) -> mi$' 'Patterns\.Call\(_, mi, _\) -> Some mi')"

# One place tuples nest past seven: the Tuples module.
check "a hand-rolled tuple nesting past seven (use Tuples)" \
  "$(violations 'GetGenericArguments\(\)(\.Length)? *= *8|fields\.Length = 8|elements\.Length = 8' 'if elements\.Length = 8 then elements, List\.take 7')"

# A struct target called or assigned through its box: one receiver helper.
check "a struct receiver spelled out more than once (use the receiver helper in OptionalArguments)" \
  "$(more_than_once 'Expression\.Unbox\(target\.Expression')"

if [[ $failed -eq 0 ]]; then echo "check-internals: every idiom has one home"; fi
exit $failed
