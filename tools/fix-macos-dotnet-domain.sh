#!/usr/bin/env bash
set -euo pipefail

host_name="${1:-yinxiaogoudeMacBook-Pro.local}"
local_host_name="${2:-yinxiaogoudeMacBook-Pro}"
computer_name="${3:-yinxiaogoudeMacBook-Pro}"
nis_domain="${4:-localdomain}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Re-running with sudo because macOS restricts hostname/domain changes."
  exec sudo "$0" "$host_name" "$local_host_name" "$computer_name" "$nis_domain"
fi

echo "Setting HostName=$host_name"
scutil --set HostName "$host_name"

echo "Setting LocalHostName=$local_host_name"
scutil --set LocalHostName "$local_host_name"

echo "Setting ComputerName=$computer_name"
scutil --set ComputerName "$computer_name"

echo "Setting NIS domain name=$nis_domain"
helper_dir="$(mktemp -d "${TMPDIR:-/tmp}/accesscity-domain.XXXXXX")"
trap 'rm -rf "$helper_dir"' EXIT
cat >"$helper_dir/setdomain.c" <<'C'
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv) {
  if (argc != 2) {
    fprintf(stderr, "usage: setdomain <domain>\n");
    return 2;
  }

  if (setdomainname(argv[1], (int)strlen(argv[1])) != 0) {
    fprintf(stderr, "setdomainname failed: errno=%d %s\n", errno, strerror(errno));
    return 1;
  }

  return 0;
}
C
cc -o "$helper_dir/setdomain" "$helper_dir/setdomain.c"
"$helper_dir/setdomain" "$nis_domain"

dscacheutil -flushcache || true
killall -HUP mDNSResponder 2>/dev/null || true

echo
echo "Current names:"
scutil --get HostName
scutil --get LocalHostName
scutil --get ComputerName

echo
cat >"$helper_dir/getdomain.c" <<'C'
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

int main(void) {
  char domain[256] = {0};
  int rc = getdomainname(domain, sizeof(domain));
  printf("getdomainname rc=%d domain='%s'\n", rc, rc == 0 ? domain : "");
  if (rc != 0) {
    fprintf(stderr, "getdomainname failed: errno=%d %s\n", errno, strerror(errno));
    return 1;
  }

  return 0;
}
C
cc -o "$helper_dir/getdomain" "$helper_dir/getdomain.c"
"$helper_dir/getdomain"

echo
echo "Done. Open a new terminal before re-running dotnet restore/test."
