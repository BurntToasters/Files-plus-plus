import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";

function loadDotEnv(dotEnvPath) {
  if (!existsSync(dotEnvPath)) {
    return;
  }

  const content = readFileSync(dotEnvPath, "utf8");
  const lines = content.split(/\r?\n/);
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) {
      continue;
    }

    const delimiterIndex = trimmed.indexOf("=");
    if (delimiterIndex <= 0) {
      continue;
    }

    const key = trimmed.slice(0, delimiterIndex).trim();
    if (!key || process.env[key]) {
      continue;
    }

    let value = trimmed.slice(delimiterIndex + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }

    process.env[key] = value;
  }
}

loadDotEnv(resolve(process.cwd(), ".env"));

const tag = process.argv[2] ?? process.env.FILESPP_RELEASE_TAG;
if (!tag) {
  console.error("Usage: node scripts/release-github.mjs <tag>");
  process.exit(1);
}

const rootsConfig =
  process.env.FILESPP_RELEASE_ASSET_ROOTS ?? "artifacts/msix,artifacts/msi";
const roots = rootsConfig
  .split(/[;,]/)
  .map((entry) => entry.trim())
  .filter(Boolean);

const assets = [];
for (const root of roots) {
  if (!existsSync(root)) {
    continue;
  }

  const stack = [root];
  while (stack.length > 0) {
    const current = stack.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const fullPath = join(current, entry.name);
      if (entry.isDirectory()) {
        stack.push(fullPath);
      } else if (/\.(msixbundle|msix|msi|appinstaller)$/i.test(entry.name)) {
        assets.push(fullPath);
      }
    }
  }
}

if (assets.length === 0) {
  console.error(
    `No release assets found. Searched roots: ${roots.join(", ")}`
  );
  process.exit(1);
}

const args = [
  "release",
  "create",
  tag,
  ...assets,
  "--generate-notes",
  "--title",
  `Files++ ${tag}`
];

const repo = process.env.FILESPP_GH_REPO ?? process.env.GH_REPO;
if (repo) {
  args.push("--repo", repo);
}

execFileSync("gh", args, { stdio: "inherit" });
