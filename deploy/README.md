# Pharmco POS — deployment guide (VPS)

Corrected version of the proposal's §6 (typo in the `scp` command fixed, honest
container count, secrets handling, backups documented). Total: ~2 h one-time.

## 1. Provision a VPS

Hetzner CX22 or DigitalOcean equivalent — Ubuntu 22.04, 2 vCPU / 4 GB RAM, 40 GB.
Point DNS A records at the VPS **before Caddy starts (DNS-only on Cloudflare);
turn the orange cloud ON afterwards.

## 2. First run

```bash
ssh root@your-vps-ip
git clone https://your-git-host/pharmco.git /opt/pharmco && cd /opt/pharmco/deploy
cp .env.example .env && ./scripts/gen-env.sh     # fills strong secrets, chmod 600
mkdir -p secrets && chmod 700 secrets
# Generate once; retain this private key for API restarts and provisioning.
dotnet run --project ../server/Pharmco.Cli -- gen-keys --out-dir ./secrets
chmod 600 secrets/license_private.pem
docker compose up -d --build                      # api + postgres + redis + caddy
docker compose ps                                  # all healthy?
```

Caddy auto-issues TLS for `{BASE_DOMAIN` and `api.{BASE_DOMAIN}` — first request
may take seconds for issuance.

> The API container build needs `server/` content — worktree clone, or `scp` the
> repo up. `client-publish/` must exist for the client download host:
>
> ```bash
> mkdir -p /opt/pharmco/client-publish
> # later per release:
> scp -r ./client-publish/* root@vps:/opt/pharmco/client-publish/
> ```
>
> Keep `deploy/secrets/license_private.pem` persistent and restricted to the
> API/provisioning operator. The API refuses to start in Production without
> this key and a non-development JWT secret. Embed the matching public key in
> the desktop client before provisioning production tenants.

### `db` first-boot behavior
The `db/master/*.sql` chain is mounted into `/docker-entrypoint-initdb.d/`
and creates the master tables automatically on an **empty volume only**. To apply
later (schema drift) use `docker exec db psql -U pharmco pharmco -f-` (file pipes,
`psql` from within the container) — CI is the enforcement point going forward.

## Cron (edit `crontab -e` on the VPS)
```
0 2 * * * cd /opt/pharmco/deploy && ./backup.sh >> /var/log/pharmco-backup.log 2>&1
0 5 * * 0 find /backups -name 'pharmco-*.sql.gz* -mtime +30 -delete
```

## Restore (drill this once before pilot)
```bash
./restore.sh /backups/pharmco-2026-09-14-020000.sql.gz   # .enc variants need BACKUP_PASSPHRASE
```

## Firewall
Allow 22 (lock to office IPs), 80/443 (public); everything else closed.

## Verify (smoke)
```bash
curl -fsS https://api.${BASE_DOMAIN}/health      # -> {"status":"ok"}
openssl s_client -connect ${BASE_DOMAIN}:443 -servername $BASE_DOMAIN </dev/null 2>/dev/null | openssl x509 -noout -subject
```

## Ongoing release cycle (self-updater contract: `client/` + `version.json`)

```bash
dotnet publish ../client/Pharmco.Client -c Release -o /tmp/clientbuild
# compute: sha256sum clientbuild.zip > version.json (fields: version, sha256, url)
scp -r /tmp/clientbuild/* root@vps:/opt/pharmco/client-publish/ && scp version.json root@vps:/opt/pharmco/client-publish/
```
