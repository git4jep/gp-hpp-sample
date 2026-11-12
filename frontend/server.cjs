const http = require("node:http");
const https = require("node:https");
const fs = require("node:fs");
const path = require("node:path");
const { pipeline } = require("node:stream");

const root = path.resolve(__dirname, "browser");
const api = new URL(process.env.API_BASE_URL);
const publicOrigin = new URL(process.env.PUBLIC_ORIGIN).origin;
if (api.protocol !== "https:" && api.hostname !== "127.0.0.1") {
  throw new Error("API_BASE_URL must use HTTPS.");
}
const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
};
const customerRoute = /^(?:\/api\/(?:config$|checkout$|orders\/[^/]+(?:\/reconcile)?$|demo\/orders\/[^/]+\/complete$|payments\/(?:cancel|notifications)\/[^/]+$)|\/payments\/(?:return|cancel)\/[^/]+$|\/health\/ready$)/;
const hopHeaders = new Set([
  "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
  "te", "trailer", "transfer-encoding", "upgrade",
]);

http.createServer((req, res) => {
  res.setHeader("Referrer-Policy", "no-referrer");
  res.setHeader("X-Content-Type-Options", "nosniff");
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(req.url, publicOrigin).pathname);
  } catch {
    res.writeHead(400).end();
    return;
  }
  if (customerRoute.test(pathname)) {
    if (!["GET", "HEAD", "POST"].includes(req.method)) {
      res.writeHead(405).end();
      return;
    }
    // Validate browser callers before adapting the origin for the existing API.
    if (req.headers.origin && req.headers.origin !== publicOrigin) {
      res.writeHead(403).end();
      return;
    }
    const headers = Object.fromEntries(Object.entries(req.headers)
      .filter(([name]) => !hopHeaders.has(name) &&
        !["host", "authorization", "cookie", "forwarded", "referer"].includes(name) &&
        !name.startsWith("x-forwarded-")));
    headers.host = api.host;
    if (headers.origin) headers.origin = api.origin;
    const transport = api.protocol === "https:" ? https : http;
    const upstream = transport.request({
      protocol: api.protocol, hostname: api.hostname, port: api.port,
      path: req.url, method: req.method, headers,
    }, (response) => {
      res.writeHead(response.statusCode, Object.fromEntries(
        Object.entries(response.headers).filter(([name]) =>
          !hopHeaders.has(name) && name !== "set-cookie")));
      pipeline(response, res, () => {});
    });
    upstream.setTimeout(60000, () => upstream.destroy());
    upstream.on("error", () => {
      if (!res.headersSent) {
        res.writeHead(502, { "Content-Type": "text/plain" })
          .end("The merchant API is unavailable.");
      } else res.destroy();
    });
    pipeline(req, upstream, () => {});
    return;
  }
  if (pathname.startsWith("/api/") || pathname.startsWith("/payments/")) {
    res.writeHead(404).end();
    return;
  }
  if (!["GET", "HEAD"].includes(req.method)) {
    res.writeHead(405).end();
    return;
  }
  const filename = path.resolve(root, "." + pathname);
  if (!filename.startsWith(root + path.sep) && filename !== root) {
    res.writeHead(404).end();
    return;
  }
  fs.stat(filename, (error, stat) => {
    const target = !error && stat.isFile() ? filename
      : !path.extname(pathname) ? path.join(root, "index.html") : null;
    if (!target) {
      res.writeHead(404).end();
      return;
    }
    fs.stat(target, (error, stat) => {
      if (error) {
        res.writeHead(404).end();
        return;
      }
      res.writeHead(200, {
        "Content-Type": types[path.extname(target)] || "application/octet-stream",
        "Content-Length": stat.size,
        "Cache-Control": target.endsWith(".html") ? "no-cache"
          : "public, max-age=3600",
      });
      if (req.method === "HEAD") res.end();
      else pipeline(fs.createReadStream(target), res, () => {});
    });
  });
}).listen(Number(process.env.PORT || 8080), "0.0.0.0");
