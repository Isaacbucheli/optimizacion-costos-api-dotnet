/*
 * Ejecuta la capa de dibujo del artefacto HTML del informe de valor y devuelve, por stdout, lo que
 * quedo escrito en cada nodo con id. Lo llama RenderDelArtefactoTests desde .NET:
 *
 *     node render-artefacto.mjs <ruta del artefacto .html>
 *
 * POR QUE EXISTE. Los otros tests de la plantilla son barridos de TEXTO (ver el docstring de
 * PlantillaCapaDeDibujoTests y de ContratoEntreRenderizadoresTests): auditan nombres de campo sin
 * ejecutar el JavaScript. Eso no puede ver dos clases de defecto que este modulo ya pago:
 *
 *   1. render() REVIENTA con un modelo legitimo y el artefacto sale con los contadores del hero
 *      congelados en su "0" literal y tres secciones vacias. La generacion devuelve 200 y la
 *      entrega queda archivada como exitosa.
 *   2. Una cifra vacia se publica como "0.00 %" en vez de decir por que esta vacia. El texto
 *      publicado es lo unico que prueba cual de las dos cosas hizo el dibujo.
 *
 * Los dos se ven mirando lo que el artefacto IMPRIME, asi que aca se ejecuta de verdad.
 *
 * EL DOM ES UN SUSTITUTO MINIMO, no jsdom (este repo no tiene package.json y no va a tener uno por
 * un test). Lo que emula, y por que alcanza:
 *
 *   - querySelector('#id') devuelve un nodo SOLO si ese id existe en el HTML del artefacto, igual
 *     que el navegador. Es lo que hace que las guardas reales de la plantilla se ejerciten: en el
 *     artefacto exportado no existen #gate ni #btn-exp (viven en la zona de carga, que el
 *     exportador saca), asi que revisaGate() tiene que salir por su `if(!g) return`, y las tablas
 *     interactivas por su `if(!tb) return` porque sus ids nacen dentro de un innerHTML.
 *   - innerHTML y textContent se guardan como texto, no se parsean. Por eso querySelectorAll()
 *     devuelve vacio: los nodos que la plantilla crea con innerHTML no existen como objetos. La
 *     consecuencia es que animarTodo() no encuentra sus .cnt y no anima nada -- no importa, lo que
 *     se audita es el HTML que dejo escrito el hero, con su data-v, no el numero animado.
 *   - Los nodos SVG (createElementNS) son objetos con setAttribute/appendChild/classList: los
 *     graficos se construyen completos y cualquier lectura invalida de un dato revienta igual que
 *     en el navegador, que es justo lo que se quiere.
 *
 * Salida (JSON en una linea):
 *   { ok, error, stack, elementos: { <id>: { html, texto } } }
 * ok=false con error/stack es un render() que se cayo: el artefacto queda a medias.
 */
import { readFileSync } from "node:fs";
import vm from "node:vm";

const ruta = process.argv[2];
if (!ruta) {
  process.stdout.write(JSON.stringify({ ok: false, error: "falta la ruta del artefacto", elementos: {} }));
  process.exit(0);
}

const html = readFileSync(ruta, "utf8");

/** Los dos bloques <script> del artefacto: el de datos y la capa de dibujo. */
function extraerScripts(fuente) {
  const abreDatos = '<script id="data">';
  const i1 = fuente.indexOf(abreDatos);
  if (i1 < 0) throw new Error("el artefacto no tiene el bloque <script id=\"data\">");
  const f1 = fuente.indexOf("</script>", i1);
  const datos = fuente.slice(i1 + abreDatos.length, f1);

  const i2 = fuente.indexOf("<script>", f1);
  if (i2 < 0) throw new Error("el artefacto no tiene el script de la capa de dibujo");
  const f2 = fuente.indexOf("</script>", i2);
  return { datos, app: fuente.slice(i2 + "<script>".length, f2) };
}

/** Todos los id="..." que existen de verdad en el marcado del artefacto. */
function idsDelMarcado(fuente) {
  const ids = new Set();
  for (const m of fuente.matchAll(/\sid="([^"]+)"/g)) ids.add(m[1]);
  return ids;
}

const ids = idsDelMarcado(html);
const registro = new Map();

function nuevoElemento(tag, id) {
  const clases = new Set();
  const attrs = new Map();
  const el = {
    tagName: String(tag).toUpperCase(),
    id: id || "",
    innerHTML: "",
    textContent: "",
    value: "",
    disabled: false,
    className: "",
    scrollTop: 0,
    scrollHeight: 0,
    dataset: {},
    style: {},
    hijos: [],
    parentNode: null,
    classList: {
      add(...c) { c.forEach((x) => clases.add(x)); },
      remove(...c) { c.forEach((x) => clases.delete(x)); },
      contains(c) { return clases.has(c); },
      toggle(c, forzar) {
        const on = forzar === undefined ? !clases.has(c) : !!forzar;
        if (on) clases.add(c); else clases.delete(c);
        return on;
      },
    },
    appendChild(hijo) { el.hijos.push(hijo); if (hijo) hijo.parentNode = el; return hijo; },
    removeChild(hijo) { el.hijos = el.hijos.filter((h) => h !== hijo); return hijo; },
    setAttribute(k, v) { attrs.set(String(k), String(v)); },
    getAttribute(k) { return attrs.has(String(k)) ? attrs.get(String(k)) : null; },
    removeAttribute(k) { attrs.delete(String(k)); },
    addEventListener() {},
    removeEventListener() {},
    querySelector() { return null; },
    querySelectorAll() { return []; },
    cloneNode() { return nuevoElemento(tag, id); },
    get outerHTML() { return el.innerHTML; },
  };
  return el;
}

function porId(id) {
  if (!ids.has(id)) return null; // igual que el navegador: ese nodo no existe en el artefacto
  if (!registro.has(id)) registro.set(id, nuevoElemento("div", id));
  return registro.get(id);
}

const documento = {
  title: "",
  querySelector(sel) {
    return typeof sel === "string" && sel.startsWith("#") ? porId(sel.slice(1)) : null;
  },
  // Ver el docstring: los nodos creados por innerHTML no existen como objetos, asi que ningun
  // selector de clase o de etiqueta puede devolver algo. Se declara en vez de fingir.
  querySelectorAll() { return []; },
  createElement(tag) { return nuevoElemento(tag); },
  createElementNS(_ns, tag) { return nuevoElemento(tag); },
  createTextNode(t) { const e = nuevoElemento("text"); e.textContent = t; return e; },
  addEventListener() {},
  get documentElement() { return nuevoElemento("html"); },
  get body() { return nuevoElemento("body"); },
};

class ObservadorFalso {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const contexto = {
  document: documento,
  IntersectionObserver: ObservadorFalso,
  ResizeObserver: ObservadorFalso,
  requestAnimationFrame() { return 0; },
  cancelAnimationFrame() {},
  setTimeout() { return 0; },
  clearTimeout() {},
  console,
  URL: { createObjectURL() { return "blob:falso"; }, revokeObjectURL() {} },
  Blob: class { constructor(partes) { this.partes = partes; } },
  navigator: { userAgent: "node" },
  location: { href: "file://artefacto" },
};
contexto.window = contexto;
contexto.globalThis = contexto;
contexto.innerWidth = 1440;
contexto.innerHeight = 900;

const salida = { ok: true, error: null, stack: null, elementos: {} };
try {
  const { datos, app } = extraerScripts(html);
  const sandbox = vm.createContext(contexto);
  vm.runInContext(datos, sandbox, { filename: "artefacto-datos.js" });
  vm.runInContext(app, sandbox, { filename: "artefacto-dibujo.js" });
} catch (e) {
  salida.ok = false;
  salida.error = e && e.message ? String(e.message) : String(e);
  salida.stack = e && e.stack ? String(e.stack).split("\n").slice(0, 6).join(" | ") : null;
}

// Lo escrito hasta donde llego el dibujo, incluso si se cayo: es exactamente lo que vería el
// cliente al abrir el archivo.
for (const [id, el] of registro) salida.elementos[id] = { html: el.innerHTML, texto: el.textContent };
salida.titulo = documento.title;

process.stdout.write(JSON.stringify(salida));
