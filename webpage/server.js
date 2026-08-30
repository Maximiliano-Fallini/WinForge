const http = require("http");
const fs = require("fs");
const path = require("path");

const root = __dirname;
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".htm": "text/html",
  ".css": "text/css",
  ".js": "text/javascript",
  ".json": "application/json",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".jpg": "image/jpeg",
  ".ico": "image/x-icon"
};

const server = http.createServer((req, res) => {
  const urlPath = decodeURIComponent(req.url.split("?")[0]);
  let filePath = path.join(root, urlPath === "/" ? "index.html" : urlPath);
  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end("404 Not Found");
      return;
    }
    const ext = path.extname(filePath).toLowerCase();
    res.writeHead(200, { "Content-Type": MIME[ext] || "application/octet-stream" });
    res.end(data);
  });
});

server.listen(process.env.PORT || 8777, () => {
  console.log("Servidor en http://localhost:" + (process.env.PORT || 8777));
});