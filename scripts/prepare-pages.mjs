import fs from "node:fs";
import path from "node:path";

const values = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, args) => {
  if (index % 2 === 0) pairs.push([value.replace(/^--/, ""), args[index + 1]]);
  return pairs;
}, []));
const { admin, listener, output, repository } = values;
const apiUrl = values["api-url"];
if (!admin || !listener || !output || !repository || !apiUrl || !/^[A-Za-z0-9._-]+$/.test(repository))
  throw new Error("Provide --admin, --listener, --output, --repository and --api-url.");
const api = new URL(apiUrl);
if (api.protocol !== "https:" || api.pathname !== "/" || api.search || api.hash || api.username || api.password)
  throw new Error("--api-url must be a public HTTPS origin without a path.");

const base = `/${repository}/`;
fs.mkdirSync(output, { recursive: true });
for (const [source, name] of [[admin, "admin"], [listener, "listen"]]) {
  const target = path.join(output, name);
  fs.cpSync(source, target, { recursive: true });
  const index = path.join(target, "index.html");
  let html = fs.readFileSync(index, "utf8");
  const marker = '<base href="/" />';
  if (html.split(marker).length !== 2) throw new Error(`Expected exactly one base href in ${index}`);
  html = html.replace(marker, `<base href="${base}${name}/" />`);
  html = html.replace("</head>", `<script>const route=sessionStorage.getItem("ct-pages-route");
if(route&&route.startsWith(document.baseURI)){
sessionStorage.removeItem("ct-pages-route");history.replaceState(null,"",route);}</script>\n</head>`);
  fs.writeFileSync(index, html);
  fs.writeFileSync(path.join(target, "appsettings.json"), JSON.stringify({ ApiBaseUrl: api.origin + "/" }, null, 2) + "\n");
}

fs.writeFileSync(path.join(output, ".nojekyll"), "");
fs.writeFileSync(path.join(output, "index.html"), `<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="refresh" content="0;url=admin/"><title>ChurchTranslate</title></head>
<body><a href="admin/">Open ChurchTranslate</a></body></html>\n`);
fs.writeFileSync(path.join(output, "404.html"), `<!doctype html><html><head><meta charset="utf-8"><title>ChurchTranslate</title>
<script>const base=${JSON.stringify(base)};const path=location.pathname;
const app=path.startsWith(base+"listen/")?"listen":"admin";
sessionStorage.setItem("ct-pages-route",location.href);
location.replace(base+app+"/");</script></head><body></body></html>\n`);
