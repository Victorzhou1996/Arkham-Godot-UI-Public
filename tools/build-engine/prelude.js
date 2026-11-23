// Browser objects arkham.build touches at load time, for a host without a browser (Jint / a bare VM).
// Only defined when missing; nothing here affects the rules, which never read them.
(function (g) {
  if (typeof g.window === "undefined") g.window = g;
  if (typeof g.self === "undefined") g.self = g;
  if (typeof g.console === "undefined") g.console = { log() {}, warn() {}, error() {}, info() {}, debug() {}, time() {}, timeEnd() {} };
  if (typeof g.BroadcastChannel === "undefined") g.BroadcastChannel = function () { this.postMessage = function () {}; this.close = function () {}; this.addEventListener = function () {}; this.onmessage = null; };
  const mem = function () { const d = {}; return { getItem: (k) => (k in d ? d[k] : null), setItem: (k, v) => { d[k] = String(v); }, removeItem: (k) => { delete d[k]; }, clear: () => { for (const k in d) delete d[k]; }, key: (i) => Object.keys(d)[i] ?? null, get length() { return Object.keys(d).length; } }; };
  if (typeof g.localStorage === "undefined") g.localStorage = mem();
  if (typeof g.sessionStorage === "undefined") g.sessionStorage = mem();
  if (typeof g.navigator === "undefined") g.navigator = { language: "zh-CN", languages: ["zh-CN"], userAgent: "build-engine", onLine: false };
  if (typeof g.location === "undefined") g.location = { href: "https://localhost/", origin: "https://localhost", pathname: "/", search: "", hash: "", host: "localhost", protocol: "https:" };
  if (typeof g.addEventListener === "undefined") { g.addEventListener = function () {}; g.removeEventListener = function () {}; g.dispatchEvent = function () { return true; }; }
  if (typeof g.document === "undefined") g.document = { addEventListener() {}, removeEventListener() {}, visibilityState: "visible", documentElement: { lang: "zh-CN", style: {} }, createElement: () => ({ style: {}, setAttribute() {}, appendChild() {} }), head: { appendChild() {} }, body: { appendChild() {} } };
  if (typeof g.setTimeout === "undefined") { let id = 0; g.setTimeout = function (fn) { id++; Promise.resolve().then(() => fn()); return id; }; g.clearTimeout = function () {}; g.setInterval = function () { return 0; }; g.clearInterval = function () {}; }
  if (typeof g.queueMicrotask === "undefined") g.queueMicrotask = (fn) => Promise.resolve().then(fn);
  if (typeof g.structuredClone === "undefined") g.structuredClone = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v)));
  if (typeof g.crypto === "undefined") g.crypto = {};
  if (typeof g.crypto.getRandomValues === "undefined") g.crypto.getRandomValues = function (a) { for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 256); return a; };
  if (typeof g.crypto.randomUUID === "undefined") g.crypto.randomUUID = function () { const h = "0123456789abcdef"; let s = ""; for (let i = 0; i < 36; i++) s += i === 8 || i === 13 || i === 18 || i === 23 ? "-" : i === 14 ? "4" : h[Math.floor(Math.random() * 16)]; return s; };
  if (typeof g.performance === "undefined") g.performance = { now: () => Date.now(), mark() {}, measure() {} };
  if (typeof g.TextEncoder === "undefined") g.TextEncoder = function () { this.encode = (s) => { const u = unescape(encodeURIComponent(s)); const a = new Uint8Array(u.length); for (let i = 0; i < u.length; i++) a[i] = u.charCodeAt(i); return a; }; };
  if (typeof g.TextDecoder === "undefined") g.TextDecoder = function () { this.decode = (a) => { let s = ""; for (let i = 0; i < a.length; i++) s += String.fromCharCode(a[i]); try { return decodeURIComponent(escape(s)); } catch (e) { return s; } }; };
  if (typeof g.Intl === "undefined") g.Intl = {};
  // collation: the host may provide __hostCompare(a, b, locale) (C# CultureInfo); otherwise plain code-point order
  if (typeof g.Intl.Collator === "undefined") {
    g.Intl.Collator = function (locale) { this.compare = (a, b) => (typeof g.__hostCompare === "function" ? g.__hostCompare(String(a), String(b), String(locale || "")) : a < b ? -1 : a > b ? 1 : 0); this.resolvedOptions = () => ({ locale: locale || "zh-CN" }); };
  }
  if (typeof g.Intl.NumberFormat === "undefined") g.Intl.NumberFormat = function () { this.format = (n) => String(n); };
  if (typeof g.Intl.DateTimeFormat === "undefined") g.Intl.DateTimeFormat = function () { this.format = (d) => String(d); this.resolvedOptions = () => ({ timeZone: "UTC" }); };
  if (typeof g.Intl.PluralRules === "undefined") g.Intl.PluralRules = function () { this.select = () => "other"; };
  if (typeof g.Intl.ListFormat === "undefined") g.Intl.ListFormat = function () { this.format = (a) => a.join("、"); };
  if (typeof g.Intl.Segmenter === "undefined") g.Intl.Segmenter = function () { this.segment = (s) => Array.from(s).map((c, i) => ({ segment: c, index: i })); };
})(typeof globalThis !== "undefined" ? globalThis : this);
