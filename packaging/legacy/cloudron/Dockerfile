# This standard ChaptarrNG image tag is a source candidate. Before a Cloudron update
# is accepted, replace it with the exact published multi-architecture digest
# that passed the migration and rollback rehearsals.
FROM ghcr.io/snapetech-org/chaptarrng:latest
USER root

# Cloudron requires a 2xx health response and the package has no separate
# migration readiness window. Keep a local proxy on the stable port while the
# app starts on 8788.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash coreutils findutils mawk nginx-light sqlite3 tar \
    && rm -rf /var/lib/apt/lists/*

COPY nginx.conf /etc/nginx/nginx.conf
COPY migration-unavailable.html /usr/share/nginx/html/bookshelfng-unavailable.html
COPY cloudron-entrypoint.sh /usr/local/bin/chaptarr-cloudron-entrypoint
COPY cloudron-backup.sh /usr/local/bin/chaptarr-cloudron-backup
COPY cloudron-restore.sh /usr/local/bin/chaptarr-cloudron-restore
RUN chmod 0755 \
    /usr/local/bin/chaptarr-cloudron-entrypoint \
    /usr/local/bin/chaptarr-cloudron-backup \
    /usr/local/bin/chaptarr-cloudron-restore

# Keep the previous Cloudron app identity and hand its existing /config volume
# to the supervised first-start migration. The old Cloudron image used native
# Hardcover mode; saved runtime environment values still override that default.
ENV CHAPTARR_BOOKSHELF_COMPATIBILITY=true \
    CHAPTARR_DEFAULT_PORT=8788 \
    CHAPTARR_EXPECTED_PORT=8788 \
    CHAPTARR__SERVER__PORT=8788 \
    CHAPTARR__SERVER__BINDADDRESS=127.0.0.1 \
    HARDCOVER=true \
    HARDCOVER_NATIVE=true \
    BOOKSHELFNG_MIGRATION_MODE=automatic

EXPOSE 8787

HEALTHCHECK --interval=30s --timeout=10s --start-period=245m --retries=3 \
    CMD wget --no-proxy --quiet --output-document=/dev/null http://127.0.0.1:8787/ping

# Replacing ENTRYPOINT clears the base image's inherited application CMD.
# Pass the Chaptarr command explicitly so the supervisor actually starts it.
CMD ["dotnet", "/app/Chaptarr.dll", "-nobrowser", "-data=/config"]
ENTRYPOINT ["/usr/local/bin/chaptarr-cloudron-entrypoint"]
