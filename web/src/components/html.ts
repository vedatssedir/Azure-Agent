// Azure Boards stores rich text as HTML. It is shown through an allowlist sanitizer and edited as plain text.

const ALLOWED = new Set(["P", "DIV", "SPAN", "BR", "B", "STRONG", "I", "EM", "U", "S", "UL", "OL", "LI", "A", "CODE", "PRE", "BLOCKQUOTE", "H1", "H2", "H3", "H4",
  "TABLE", "THEAD", "TBODY", "TR", "TD", "TH", "HR", "IMG"]);

function clean(node: Node, doc: Document): Node | null {
  if (node.nodeType === Node.TEXT_NODE) return doc.createTextNode(node.textContent ?? "");
  if (node.nodeType !== Node.ELEMENT_NODE) return null;
  const element = node as Element;
  const children = [...element.childNodes].map((c) => clean(c, doc)).filter((c): c is Node => c !== null);
  if (!ALLOWED.has(element.tagName)) {
    if (["SCRIPT", "STYLE", "IFRAME", "OBJECT", "EMBED"].includes(element.tagName)) return null;
    const fragment = doc.createDocumentFragment();
    children.forEach((c) => fragment.appendChild(c));
    return fragment;
  }
  const copy = doc.createElement(element.tagName);
  if (element.tagName === "A") {
    const href = element.getAttribute("href") ?? "";
    if (/^(https?:|mailto:)/i.test(href)) { copy.setAttribute("href", href); copy.setAttribute("target", "_blank"); copy.setAttribute("rel", "noreferrer noopener"); }
  }
  if (element.tagName === "IMG") {
    const src = element.getAttribute("src") ?? "";
    if (!/^https:/i.test(src)) return null;
    copy.setAttribute("src", src);
    copy.setAttribute("alt", element.getAttribute("alt") ?? "");
    copy.setAttribute("style", "max-width:100%");
  }
  children.forEach((c) => copy.appendChild(c));
  return copy;
}

export function sanitizeHtml(html: string): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  const out = document.implementation.createHTMLDocument("");
  const root = out.createElement("div");
  [...doc.body.childNodes].map((c) => clean(c, out)).forEach((c) => { if (c) root.appendChild(c); });
  return root.innerHTML;
}

export function htmlToText(html: string): string {
  if (!html) return "";
  const doc = new DOMParser().parseFromString(html.replace(/<br\s*\/?>/gi, "\n").replace(/<\/(div|p|li|h\d|tr)>/gi, "\n"), "text/html");
  return (doc.body.textContent ?? "").replace(/ /g, " ").replace(/\n{3,}/g, "\n\n").trim();
}
