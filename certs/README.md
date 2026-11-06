# TLS certificate for the portal

Put the PFX file of the portal certificate here as `portal.pfx` (or change `ASPNETCORE_Kestrel__Certificates__Default__Path` in `.env`).
Set its password in `.env` (`ASPNETCORE_Kestrel__Certificates__Default__Password`).

The folder is mounted read-only into the portal container at `/https`. Certificate files are excluded from git.
