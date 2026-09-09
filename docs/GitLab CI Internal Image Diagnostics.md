# GitLab CI Internal Image Diagnostics

This temporary diagnostic is intended only for an organisation-hosted GitLab CI
feature branch. It does not change local application execution, local
`appsettings.json`, or the production application configuration.

## Create the feature branch

Create the branch from the organisation repository's `develop` branch:

```bash
git switch develop
git pull --ff-only origin develop
git switch -c feature/snapshot-gitlab-ci-diagnostics
```

## Add the temporary diagnostic job

Add the following top-level job to the organisation repository's
`.gitlab-ci.yml`. Replace `<internal-dotnet10-sql-image>` with the currently
approved internal image and tag.

```yaml
ci-image-diagnostics:
  stage: build
  image: <internal-dotnet10-sql-image>

  # Do not inherit installation/setup commands from a default template.
  before_script: []

  # Run only on GitFlow feature branches.
  rules:
    - if: '$CI_COMMIT_BRANCH =~ /^feature\/.+$/'
      when: on_success
    - when: never

  # This temporary diagnostic should not block the feature pipeline.
  allow_failure: true

  script:
    - |
      echo "=== Operating system ==="
      if [ -r /etc/os-release ]; then
        cat /etc/os-release
      else
        echo "/etc/os-release is unavailable"
      fi

    - |
      echo "=== Shell ==="
      echo "SHELL=${SHELL:-not-set}"
      readlink /proc/$$/exe 2>/dev/null || true

    - |
      echo "=== .NET ==="
      command -v dotnet || true
      dotnet --info || true

    - |
      echo "=== SQL tooling ==="
      echo "sqlcmd from PATH:"
      command -v sqlcmd || true

      echo "Known sqlcmd locations:"
      for path in \
        /opt/mssql-tools18/bin/sqlcmd \
        /opt/mssql-tools/bin/sqlcmd
      do
        if [ -x "$path" ]; then
          echo "$path"
          "$path" -? >/dev/null 2>&1 || true
        fi
      done

      echo "sqlpackage from PATH:"
      command -v sqlpackage || true
      sqlpackage /Version || true

    - |
      echo "=== Available utility and package commands ==="
      for tool in curl wget openssl jq unzip apt-get apk dnf yum microdnf tdnf
      do
        location="$(command -v "$tool" 2>/dev/null || true)"
        if [ -n "$location" ]; then
          echo "$tool: $location"
        else
          echo "$tool: unavailable"
        fi
      done

    - |
      echo "=== UBS certificate helper ==="
      if [ -f /ubs/ubs-certs.sh ]; then
        echo "/ubs/ubs-certs.sh is present"
      else
        echo "/ubs/ubs-certs.sh is unavailable"
      fi

    - echo "CI image diagnostics completed"
```

The job intentionally:

- installs nothing;
- starts no SQL Server or Azurite service;
- prints no environment variables or secrets;
- runs only for branches named `feature/...`; and
- cannot block the rest of the feature pipeline.

## Push the diagnostic branch

```bash
git add .gitlab-ci.yml
git commit -m "chore(ci): inspect approved integration test image"
git push -u origin feature/snapshot-gitlab-ci-diagnostics
```

In GitLab, open **Build > Pipelines**, select the feature-branch pipeline, and
open `ci-image-diagnostics`. Capture the operating-system details and the
reported locations or availability of `sqlcmd`, `sqlpackage`, and the listed
utility commands.

Do not merge this diagnostic job into `develop`. Use its output to design the
final CI-only SQL Server and Azurite configuration, then replace or remove the
temporary job on the same feature branch.

## Interpret the SQL image diagnostic

The initial organisation-runner diagnostic reported:

- Ubuntu 24.04 with Bash;
- .NET SDK 10.0.201;
- `sqlcmd` at `/opt/mssql-tools18/bin/sqlcmd` and already on `PATH`;
- `sqlpackage` installed as a .NET tool;
- `curl`, `wget`, `openssl`, and `apt-get` available; and
- `/ubs/ubs-certs.sh` present.

The final integration-test job should therefore use the approved SQL-enabled
.NET image directly. It does not need to configure Microsoft's package
repository or install `mssql-tools18` at runtime.

## Inspect the internal Azurite image

Add this second temporary top-level job. Replace `<internal-azurite-image>`
with the approved internal image and tag. The entrypoint override stops the
normal server process so GitLab can execute the diagnostic script instead.

```yaml
azurite-image-diagnostics:
  stage: test

  image:
    name: <internal-azurite-image>
    entrypoint: [""]

  before_script: []

  rules:
    - if: '$CI_COMMIT_BRANCH =~ /^feature\/.+$/'
      when: on_success
    - when: never

  allow_failure: true

  script:
    - |
      echo "=== Operating system ==="
      if [ -r /etc/os-release ]; then
        cat /etc/os-release
      else
        echo "/etc/os-release is unavailable"
      fi

    - |
      echo "=== Shells ==="
      echo "Current shell: ${SHELL:-not-set}"
      command -v sh || true
      command -v bash || true
      readlink /proc/$$/exe 2>/dev/null || true

    - |
      echo "=== Azurite executables ==="
      for tool in azurite azurite-blob azurite-queue azurite-table
      do
        location="$(command -v "$tool" 2>/dev/null || true)"

        if [ -n "$location" ]; then
          echo "$tool: $location"
          "$tool" --version 2>/dev/null || true
        else
          echo "$tool: unavailable"
        fi
      done

    - |
      echo "=== Node runtime ==="
      command -v node || true
      node --version 2>/dev/null || true
      command -v npm || true
      npm --version 2>/dev/null || true

    - |
      echo "=== Available utilities ==="
      for tool in curl wget nc netcat bash sh apt-get apk dnf yum
      do
        location="$(command -v "$tool" 2>/dev/null || true)"

        if [ -n "$location" ]; then
          echo "$tool: $location"
        else
          echo "$tool: unavailable"
        fi
      done

    - echo "Azurite image diagnostics completed"
```

If the job cannot run because the service image does not contain a shell, do
not modify or install anything in that image. Use the service-level diagnostic
below and continue treating the image as a black box.

## Test the default Azurite service behaviour

This job starts the unmodified internal Azurite image as a service. All probes
run from the approved SQL-enabled .NET job image; nothing is installed or run
inside the Azurite container manually.

```yaml
azurite-service-diagnostics:
  stage: test

  image: <internal-dotnet10-sql-image>

  services:
    - name: <internal-azurite-image>
      alias: azurite

  before_script:
    - /bin/sh /ubs/ubs-certs.sh &> /dev/null

  rules:
    - if: '$CI_COMMIT_BRANCH =~ /^feature\/.+$/'
      when: on_success
    - when: never

  allow_failure: true

  script:
    - |
      echo "=== Azurite hostname ==="
      getent hosts azurite || true

    - |
      echo "=== Azurite service ports ==="

      for port in 10000 10001 10002
      do
        reachable=false

        for attempt in $(seq 1 30)
        do
          http_code="$(
            curl \
              --silent \
              --show-error \
              --connect-timeout 2 \
              --max-time 3 \
              --output /dev/null \
              --write-out "%{http_code}" \
              "http://azurite:${port}/" 2>/dev/null || true
          )"

          if [ -n "$http_code" ] && [ "$http_code" != "000" ]; then
            echo "Port ${port} is reachable; HTTP status=${http_code}"
            reachable=true
            break
          fi

          sleep 2
        done

        if [ "$reachable" = false ]; then
          echo "Port ${port} was not reachable"
        fi
      done

    - |
      echo "Expected Azurite ports:"
      echo "10000 = Blob"
      echo "10001 = Queue"
      echo "10002 = Table"
```

An HTTP response such as 400 or 403 proves that the port is reachable; these
diagnostic requests intentionally provide no storage credentials. Snapshot
Writer requires only the Blob endpoint on port 10000.

- If port 10000 is reachable, the image's default service configuration is
  sufficient for further Snapshot Writer CI work.
- If all three ports are reachable, the image starts the full Azurite service.
- If only port 10000 is reachable, a Blob-only configuration is sufficient.
- If no ports are reachable, inspect the service-container logs for required
  configuration while leaving the image unchanged.
