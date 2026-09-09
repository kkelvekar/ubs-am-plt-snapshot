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

