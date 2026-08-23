#!/usr/bin/env bash
# Upload one or more files as assets on a Gitea release.
#
# The replacement for `gh release upload`, ported from the media session plugin's repository. A
# script rather than a composite action: it is dry-runnable on a dev box,
# needs nothing from the runner, and is the same artefact whether a
# workflow or a human calls it.
#
# One call per file:
#
#   POST /api/v1/repos/{owner}/{repo}/releases/{id}/assets?name=<file>
#
# as `multipart/form-data` with an `attachment` field.
#
# Usage:
#   gitea-release-asset.sh --tag v1.2.3 FILE...
#   gitea-release-asset.sh --tag v1.2.3 --dry-run FILE...
#
# Options, each with an environment fallback:
#
#   --tag TAG          the release tag. No default: a release is named
#                      by its tag and guessing one is how the wrong
#                      release gets an asset
#   --release-id N     skip the tag lookup
#   --repo OWNER/NAME  default $GITHUB_REPOSITORY
#   --server URL       instance root, default $GITHUB_SERVER_URL
#   --token TOKEN      default $GITEA_TOKEN, then $GITHUB_TOKEN. Gitea's
#                      automatic token is GITEA_TOKEN, and it can write
#                      releases
#   --replace          delete an existing asset of the same name first
#   --dry-run          print what would be sent and touch no network
#
# A same-name asset is refused rather than left to the server to
# uniquify, so an unknown server behaviour becomes a loud local one.
# `--replace` is the explicit way to say "yes, overwrite that".
set -euo pipefail

tag=""
release_id=""
repo="${GITHUB_REPOSITORY:-}"
server="${GITHUB_SERVER_URL:-}"
token="${GITEA_TOKEN:-${GITHUB_TOKEN:-}}"
replace=0
dry_run=0
files=()

die() { echo "gitea-release-asset: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --tag) tag="${2:-}"; shift 2 ;;
        --release-id) release_id="${2:-}"; shift 2 ;;
        --repo) repo="${2:-}"; shift 2 ;;
        --server) server="${2:-}"; shift 2 ;;
        --token) token="${2:-}"; shift 2 ;;
        --replace) replace=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        # The header block is the help text, read to the first line that
        # is not a comment - a line range would go stale the first time
        # a paragraph is added, which it already had.
        -h|--help) awk 'NR>1 { if (!/^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit 0 ;;
        --) shift; files+=("$@"); break ;;
        -*) die "unknown option: $1" ;;
        *) files+=("$1"); shift ;;
    esac
done

[ "${#files[@]}" -gt 0 ] || die "no files given"
[ -n "$repo" ] || die "no repository: pass --repo OWNER/NAME or set GITHUB_REPOSITORY"
[ -n "$server" ] || die "no server: pass --server URL or set GITHUB_SERVER_URL"
[ -n "$tag" ] || [ -n "$release_id" ] || die "no release: pass --tag TAG or --release-id N"

# The tag goes into a URL path - `GET /releases/tags/{tag}` - so a
# character that is itself a path separator does not mean what it looks
# like. It found a real case: #255 proposed `cli/v1.2.3` for the CLI's
# tag namespace, which would not match that route unencoded and would
# put an extra segment into every asset URL. The namespace is `cli-v*`
# for that reason, and this refuses rather than quietly encoding,
# because a tag that needs encoding to be addressable is a tag someone
# should look at.
case "$tag" in
    */*) die "tag ${tag} contains a '/', which is a path separator in the release API and in every asset URL; use a namespace that is not one (cli-v1.2.3, not cli/v1.2.3)" ;;
esac

# Checked before the release is looked up, so a typo in a filename costs
# a message rather than a half-finished upload. Every flow that calls
# this has just built the files, so a missing one means the build lied.
for f in "${files[@]}"; do
    [ -f "$f" ] || die "not a file: $f"
done

if [ "$dry_run" -eq 0 ] && [ -z "$token" ]; then
    die "no token: pass --token or set GITEA_TOKEN"
fi

api="${server%/}/api/v1/repos/${repo}"

# A single curl wrapper, so the auth header and the failure handling are
# written once. --fail-with-body keeps the server's error text, which is
# the difference between "422" and "an asset with this name exists".
gitea() {
    local method="$1" url="$2"; shift 2
    curl --silent --show-error --fail-with-body \
        --request "$method" \
        --header "Authorization: token ${token}" \
        --header "Accept: application/json" \
        "$@" \
        "$url"
}

if [ "$dry_run" -eq 1 ]; then
    echo "DRY RUN - no request is made"
    echo "  server:     ${server%/}"
    echo "  repo:       ${repo}"
    if [ -n "$release_id" ]; then
        echo "  release:    id ${release_id} (given)"
    else
        echo "  release:    GET ${api}/releases/tags/${tag}  -> .id"
    fi
    echo "  token:      $([ -n "$token" ] && echo "set" || echo "MISSING (would refuse)")"
    echo "  on clash:   $([ "$replace" -eq 1 ] && echo "delete then re-upload" || echo "refuse")"
    for f in "${files[@]}"; do
        name="$(basename "$f")"
        echo "  upload:     POST ${api}/releases/{id}/assets?name=${name}"
        echo "              attachment=@${f}  ($(stat -c %s "$f") bytes)"
        # Where the asset lands on the origin. Not necessarily the URL a
        # manifest promises: the plugin flow names a CDN in front of
        # this host, on purpose, so the two differ by design rather than
        # by mistake. The real answer comes back in
        # `browser_download_url` on a live run.
        echo "              -> ${server%/}/${repo}/releases/download/${tag}/${name}  (on the origin)"
    done
    exit 0
fi

if [ -z "$release_id" ]; then
    # No fallback to creating the release. Both flows fire *from* a
    # published release, so a tag with no release means the trigger and
    # the tag disagree - which is worth stopping for, not papering over.
    release_json="$(gitea GET "${api}/releases/tags/${tag}")" \
        || die "no release for tag ${tag} (or the token cannot read it)"
    release_id="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')" \
        || die "release payload for ${tag} carried no id"
    echo "release ${tag} -> id ${release_id}"
fi

existing="$(gitea GET "${api}/releases/${release_id}/assets")" \
    || die "cannot list assets of release ${release_id}"

for f in "${files[@]}"; do
    name="$(basename "$f")"

    # `python3 - "$name"` rather than a grep: an asset name can contain
    # anything a filename can, and matching JSON with a regex is how the
    # wrong asset gets deleted.
    clash="$(printf '%s' "$existing" | python3 -c '
import json, sys
name = sys.argv[1]
for asset in json.load(sys.stdin):
    if asset.get("name") == name:
        print(asset["id"])
        break
' "$name")"

    if [ -n "$clash" ]; then
        if [ "$replace" -eq 1 ]; then
            echo "replacing ${name} (asset ${clash})"
            gitea DELETE "${api}/releases/${release_id}/assets/${clash}" >/dev/null \
                || die "cannot delete existing asset ${name}"
        else
            die "release ${tag:-$release_id} already has an asset named ${name}; pass --replace to overwrite"
        fi
    fi

    echo "uploading ${name} ($(stat -c %s "$f") bytes)"
    response="$(gitea POST "${api}/releases/${release_id}/assets?name=${name}" \
        --form "attachment=@${f}")" \
        || die "upload of ${name} failed"

    printf '%s' "$response" | python3 -c '
import json, sys
asset = json.load(sys.stdin)
print("  ->", asset.get("browser_download_url", "(no browser_download_url in the response)"))
'
done
