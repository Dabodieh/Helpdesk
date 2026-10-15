#!/usr/bin/env python3
"""Validates docker-compose.yml (Production and Development profiles) without any real credential.

A throw-away env file is generated from .env.example with obvious placeholder values for every empty setting, so interpolation of required variables is exercised
while no secret exists anywhere. Output names services and settings only, never values. Exit code 0 = every policy holds, 1 = at least one violation.

  python3 scripts/ci/validate-compose.py            # from the repository root; needs `docker compose`
"""
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROBLEMS = []


def problem(message):
    PROBLEMS.append(message)
    print(f"FAIL  {message}")


def ok(message):
    print(f"ok    {message}")


def make_env_file():
    values = {}
    with open(os.path.join(ROOT, ".env.example"), encoding="utf-8") as handle:
        for line in handle:
            match = re.match(r"^([A-Z][A-Z0-9_]*)=(.*)$", line.rstrip("\n"))
            if match:
                values[match.group(1)] = match.group(2)
    for key, value in list(values.items()):
        if value == "":
            values[key] = "ci-placeholder-" + key.lower()
    # Fixed, non-secret settings the production profile requires.
    values.setdefault("SECRETS_DIR", tempfile.gettempdir())
    values["SECRETS_DIR"] = tempfile.gettempdir() if not values["SECRETS_DIR"] else values["SECRETS_DIR"]
    values["ENTRA_TENANT_ID"] = "00000000-0000-0000-0000-000000000001"
    values["ENTRA_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002"
    values["GRAPH_TENANT_ID"] = "00000000-0000-0000-0000-000000000001"
    values["GRAPH_CLIENT_ID"] = "00000000-0000-0000-0000-000000000003"
    handle = tempfile.NamedTemporaryFile("w", suffix=".env", delete=False, encoding="utf-8")
    for key, value in values.items():
        handle.write(f"{key}={value}\n")
    handle.close()
    return handle.name


def compose_config(env_file, *profiles):
    command = ["docker", "compose", "-f", os.path.join(ROOT, "docker-compose.yml"), "--project-directory", ROOT, "--env-file", env_file]
    for profile in profiles:
        command += ["--profile", profile]
    command += ["config", "--format", "json"]
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        # compose error text names variables, not their values
        problem(f"docker compose config failed for profiles {profiles or ('default',)}: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else 'unknown error'}")
        return None
    return json.loads(result.stdout)


def main():
    env_file = make_env_file()
    try:
        for profiles in ((), ("development",), ("production",), ("production", "production-migrate"), ("production", "tunnel"), ("storage",)):
            if compose_config(env_file, *profiles) is not None:
                ok(f"compose resolves for profiles: {', '.join(profiles) or 'default'}")

        # Development and Production share a container name by design (they can never run together), so they are inspected as two configurations.
        config = compose_config(env_file, "production", "production-migrate", "tunnel")
        dev_config = compose_config(env_file, "development", "storage")
        if config is None or dev_config is None:
            return 1
        services = config["services"]
        dev_services = dev_config["services"]

        # 1. no floating images
        for name, service in services.items():
            image = service.get("image", "")
            if not image:
                continue
            if image.endswith(":latest") or (":" not in image.split("/")[-1] and "@" not in image):
                problem(f"service {name} uses a floating image reference: {image.split('@')[0]}")
        if not [p for p in PROBLEMS if "floating" in p]:
            ok("no service uses :latest or an untagged image")
        if "@sha256:" not in services["cloudflared"]["image"]:
            problem("cloudflared is not pinned by digest")
        else:
            ok("cloudflared is pinned by tag and digest")

        # 2. production services publish no ports
        for name in ("app", "postgres-prod", "objectstore-prod", "migrate", "cloudflared"):
            if services[name].get("ports"):
                problem(f"production service {name} publishes ports")
        ok("production services publish no ports") if not [p for p in PROBLEMS if "publishes ports" in p] else None

        # 3. restart policy
        expected = {"app": "unless-stopped", "postgres-prod": "unless-stopped", "objectstore-prod": "unless-stopped", "cloudflared": "no"}
        for name, policy in expected.items():
            actual = services[name].get("restart", "no")
            if actual != policy:
                problem(f"{name} restart policy is '{actual}', expected '{policy}'")
        ok("restart policies as designed (tunnel never restarts by itself)") if not [p for p in PROBLEMS if "restart policy" in p] else None

        # 4. production application settings
        env = services["app"].get("environment", {})
        checks = {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "Authentication__DevSignIn__Enabled": "false",
            "Database__MigrateOnStartup": "false",
            "Attachments__Enabled": "false",
            "Attachments__RequireCleanScan": "true",
            "DataProtection__RequireCertificate": "true",
            "Mail__Provider": "Graph",
        }
        for key, value in checks.items():
            if str(env.get(key)) != value:
                problem(f"app setting {key} is not '{value}'")
        connection = str(env.get("Database__ConnectionString", ""))
        if "Host=postgres-prod;" not in connection:
            problem("app does not connect to postgres-prod")
        if "Username=helpdesk_app;" not in connection:
            problem("app does not use the least-privilege application role by default")
        migrate_connection = str(services["migrate"].get("environment", {}).get("Database__ConnectionString", ""))
        if "Username=helpdesk_prod_admin;" not in migrate_connection:
            problem("migrate does not use the administrator role")
        if not [p for p in PROBLEMS if p.startswith("app ") or p.startswith("migrate ")]:
            ok("production application settings and database roles as designed")

        # 5. secrets are mounted read-only
        for name in ("app", "migrate"):
            mounts = [v for v in services[name].get("volumes", []) if v.get("target") == "/run/helpdesk-secrets"]
            if not mounts or not mounts[0].get("read_only"):
                problem(f"{name} does not mount the secrets folder read-only")
        ok("secrets folder is mounted read-only") if not [p for p in PROBLEMS if "read-only" in p] else None

        # 6. Development services bind to loopback only
        for name in ("postgres", "host", "web", "objectstore"):
            for port in dev_services[name].get("ports", []) or []:
                if port.get("host_ip") != "127.0.0.1":
                    problem(f"development service {name} publishes a port on a non-loopback address")
        ok("development ports bind to 127.0.0.1 only") if not [p for p in PROBLEMS if "non-loopback" in p] else None

        # 7. Production must not share the development database server
        if "postgres" in services["app"].get("depends_on", {}):
            problem("app still depends on the development postgres service")
        else:
            ok("production does not depend on the development PostgreSQL")
    finally:
        os.unlink(env_file)

    print(f"\n{len(PROBLEMS)} problem(s)")
    return 1 if PROBLEMS else 0


if __name__ == "__main__":
    sys.exit(main())
