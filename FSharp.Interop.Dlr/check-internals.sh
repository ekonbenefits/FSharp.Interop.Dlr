#!/usr/bin/env bash
# Patterns fixed more than once, kept fixed (#163): each idiom has one home, and a copy elsewhere
# fails the gate. Run from anywhere: ./FSharp.Interop.Dlr/check-internals.sh (exit 1 on any violation).
set -uo pipefail
cd "$(dirname "$0")" || exit 1
sources=(*.fs)
[[ -f "${sources[0]}" ]] || { echo "check-internals: no sources found"; exit 1; }
failed=0

# Lines matching the pattern $1 outside the top-level module named $2: the idiom's one home. A
# line belongs to the module last opened at column 0 (`module [internal] Name`); any other
# column-0 declaration (`type`, an attribute, a doc comment, `namespace`) ends it.
outside_module() {
  # The pattern through the environment: `awk -v` would process its backslashes.
  PAT="$1" HOME_MODULE="$2" awk '
    BEGIN { pat = ENVIRON["PAT"]; home = ENVIRON["HOME_MODULE"] }
    FNR == 1 { mod = "" }
    /^module / { n = split($0, w, /[ =]+/); mod = (w[2] == "internal" || w[2] == "private") ? w[3] : w[2] }
    /^(type |\[<|\/\/\/|namespace )/ { mod = "" }
    $0 ~ pat && mod != home { print FILENAME ":" FNR ":" $0 }
  ' "${sources[@]}"
}

# Lines matching $1, other than those matching $2 (a short allowlist, documented at the check).
violations() {
  grep -nE "$1" "${sources[@]}" | grep -vE "$2" || true
}

# Every line matching $1, when there is more than one.
more_than_once() {
  local found
  found="$(grep -nE "$1" "${sources[@]}" || true)"
  if [[ $(printf '%s' "$found" | grep -c .) -gt 1 ]]; then echo "$found"; fi
}

check() {
  local what="$1" found="$2"
  if [[ -n "$found" ]]; then
    echo "check-internals: $what"
    echo "$found" | sed 's/^/  /'
    failed=1
  fi
}

# A delegate type's Invoke and constructor go through DelegateMembers (#121, #150): F# compiles an
# internal delegate's members internal, and a public-only lookup returns null. Allowed only on
# types that are always public: a call site's or per-key delegate (`siteDelegate`), a factory's
# `Func` (`factoryType`), `FSharpFunc` (`funcType`, `typeof<FSharpFunc…`), a generated adapter,
# `typeof<…>`, an FSharpRef (`cell.Type`) — and DelegateMembers' own lookup.
check "a delegate's Invoke/constructor looked up outside DelegateMembers (use DelegateMembers.invokeOf/constructorOf)" \
  "$(violations '\.GetMethod\("Invoke"\)|\.GetConstructor\(' '(siteDelegate|factoryType|funcType|adapter|typeof<[^>]*>+|cell\.Type)\.Get(Method|Constructor)|GetConstructor\(flags, null')"

check "a quotation rebuilt outside Quotation.rebuild" \
  "$(outside_module 'RebuildShapeCombination' Quotation)"

check "a MethodInfo matched out of a quotation outside Quotation.methodOf" \
  "$(outside_module 'Call ?\(_, *mi, *_\) *->' Quotation)"

check "a DynamicMethod outside Emit.factory" \
  "$(outside_module 'DynamicMethod ?\(' Emit)"

check "a unit-domain strip outside FunctionShapes.parameters" \
  "$(outside_module '\[ *typeof<unit> *\] *then *\[ *\]' FunctionShapes)"

check "tuple nesting past seven outside the Tuples module" \
  "$(outside_module 'List\.(take|skip) 7|\.\[7\]|Length *(=|<=) *[78]([^0-9]|$)' Tuples)"

check "a struct receiver spelled out more than once (use the receiver helper in Fallback)" \
  "$(more_than_once 'Expression\.Unbox ?\(')"

if [[ $failed -eq 0 ]]; then echo "check-internals: every idiom has one home"; fi
exit $failed
