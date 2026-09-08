# Security policy

Hen's DNS filter and device access control are experimental. Use the latest published source revision and read the filtering and shutdown limitations in the [README](README.md).

## Reporting a vulnerability

If the repository's Security tab offers **Report a vulnerability**, use that private reporting flow. Otherwise, open an issue asking the maintainer to establish a private reporting channel, without including the exploit or sensitive details. Do not put credentials, browsing history, real device identifiers, or network configuration in a public report.

A private report should describe the affected version, required privileges, reproduction steps using synthetic data, expected behavior, and observed impact. Do not test against someone else's network or devices without authorization.

## Boundaries

- DNS rules apply only to the documented hotspot gateway DNS path. Bypasses through private/external DNS, VPNs, caches, existing connections, or direct IP addresses are known limitations, not a guarantee of full traffic isolation.
- MAC-based approvals are not personal identity verification and do not prevent spoofing or password sharing.
- Administrative helpers authenticate their named-pipe peer and use temporary filters. They keep independent two-second health checks. UI power-saving behavior must not weaken these checks.
- Filter failures attempt to stop the hotspot. Abrupt process termination can leave a gap without temporary filters; do not treat Hen as a hardened network perimeter.
- Activity is stored locally without application-level encryption. Access to the Windows account or its files may expose that history.
- A clean dependency audit and passing tests do not establish that the application is free of vulnerabilities. Native-driver changes need separate review.
