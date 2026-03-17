import type { Plugin, ViteDevServer } from "vite";

const REQUIRED_VARS = [
  "VITE_ENTRA_SPA_CLIENT_ID",
  "VITE_ENTRA_TENANT_ID",
] as const;

const ERROR_HTML = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <title>Setup Required</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body { font-family: system-ui, -apple-system, sans-serif; background: #1a1a2e; color: #e0e0e0; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 2rem; }
    .card { background: #16213e; border: 1px solid #e94560; border-radius: 12px; padding: 2.5rem; max-width: 640px; width: 100%; }
    h1 { color: #e94560; font-size: 1.5rem; margin-bottom: 0.5rem; }
    .subtitle { color: #a0a0b0; margin-bottom: 1.5rem; }
    h2 { color: #0f3460; font-size: 1.1rem; margin: 1.25rem 0 0.5rem; color: #53a8b6; }
    code { background: #0f3460; padding: 0.15em 0.4em; border-radius: 4px; font-size: 0.9em; }
    pre { background: #0f3460; padding: 1rem; border-radius: 8px; overflow-x: auto; margin: 0.5rem 0; font-size: 0.85rem; line-height: 1.6; }
    .missing { color: #e94560; font-weight: 600; }
    ol { padding-left: 1.25rem; line-height: 1.8; }
    .help { margin-top: 1.5rem; padding-top: 1rem; border-top: 1px solid #0f3460; color: #a0a0b0; font-size: 0.85rem; }
    a { color: #53a8b6; }
  </style>
</head>
<body>
  <div class="card">
    <h1>⚠️ Setup Required</h1>
    <p class="subtitle">Missing environment variables needed for Entra ID authentication.</p>

    <p>The following variables are <span class="missing">not set</span>:</p>
    <pre>MISSING_VARS_PLACEHOLDER</pre>

    <h2>How to fix</h2>
    <ol>
      <li>Run <code>azd up</code> from the repo root — this creates the Entra app registration and generates the required <code>.env</code> files automatically.</li>
      <li>Restart the dev server after <code>azd up</code> completes.</li>
    </ol>

    <h2>Coming from the AI Foundry portal?</h2>
    <p>The portal's "View sample app code" gives you AI resource variables, but this app also needs an <strong>Entra ID app registration</strong> for authentication. Running <code>azd up</code> creates it for you — even if your AI Foundry resources already exist.</p>

    <pre>azd up</pre>

    <div class="help">
      📖 See the <a href="https://github.com/microsoft-foundry/foundry-agent-webapp#quick-start">README</a> for full setup instructions.
    </div>
  </div>
</body>
</html>`;

export function envCheckPlugin(): Plugin {
  let missing: string[] = [];

  return {
    name: "env-check",
    configResolved(config) {
      if (config.command !== "serve") return;

      missing = REQUIRED_VARS.filter(
        (v) => !process.env[v] || process.env[v] === "undefined"
      );

      if (missing.length > 0) {
        const border = "━".repeat(60);
        console.warn(`\n\x1b[31m${border}\x1b[0m`);
        console.warn(`\x1b[31m  ⚠️  SETUP REQUIRED\x1b[0m`);
        console.warn(`\x1b[31m${border}\x1b[0m\n`);
        console.warn(
          `  Missing environment variables:\n${missing.map((v) => `    • ${v}`).join("\n")}\n`
        );
        console.warn(`  Run \x1b[36mazd up\x1b[0m from the repo root to create the`);
        console.warn(`  Entra app registration and generate .env files.\n`);
        console.warn(
          `  Coming from the AI Foundry portal? You still need to`
        );
        console.warn(
          `  run \x1b[36mazd up\x1b[0m — the portal gives AI resource vars, but`
        );
        console.warn(
          `  this app also requires an Entra ID app for authentication.\n`
        );
        console.warn(`\x1b[31m${border}\x1b[0m\n`);
      }
    },
    configureServer(server: ViteDevServer) {
      if (missing.length === 0) return;

      server.middlewares.use((_req, res, next) => {
        if (
          _req.url?.startsWith("/@") ||
          _req.url?.startsWith("/__") ||
          _req.url?.startsWith("/api")
        ) {
          next();
          return;
        }

        const html = ERROR_HTML.replace(
          "MISSING_VARS_PLACEHOLDER",
          missing.join("\n")
        );

        res.statusCode = 503;
        res.setHeader("Content-Type", "text/html");
        res.end(html);
      });
    },
  };
}
