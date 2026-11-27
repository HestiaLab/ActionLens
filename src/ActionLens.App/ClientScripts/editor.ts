// CodeMirror 6 host for ActionLens's workflow and event-payload editors.
//
// Blazor calls these exports through IJSObjectReference (see CodeEditor.razor.cs). The module is
// deliberately thin: it owns the CodeMirror view lifecycle and the 1-based line/column arithmetic the
// engine's SourceRange uses, and pushes text changes back to .NET, where the analysis actually runs.
import {
  Compartment,
  EditorState,
  StateEffect,
  StateField,
  type Extension,
} from '@codemirror/state';
import {
  Decoration,
  EditorView,
  keymap,
  lineNumbers,
  highlightActiveLine,
  highlightActiveLineGutter,
  drawSelection,
  dropCursor,
  rectangularSelection,
  type DecorationSet,
  type ViewUpdate,
} from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import {
  bracketMatching,
  defaultHighlightStyle,
  foldGutter,
  indentOnInput,
  syntaxHighlighting,
} from '@codemirror/language';
import { searchKeymap, highlightSelectionMatches } from '@codemirror/search';
import { autocompletion, closeBrackets, closeBracketsKeymap, completionKeymap } from '@codemirror/autocomplete';
import { json } from '@codemirror/lang-json';
import { yaml } from '@codemirror/lang-yaml';

/** The subset of DotNetObjectReference this module uses, declared locally so the bundle needs no interop typings. */
interface DotNetTarget {
  invokeMethodAsync(method: string, ...args: unknown[]): Promise<unknown>;
}

interface EditorOptions {
  /** Initial document text. */
  doc: string;
  /** `'yaml'` or `'json'`; anything else leaves the language off. */
  language: string;
  /** Accessible name for the editable region. */
  ariaLabel: string;
}

interface Held {
  view: EditorView;
  language: Compartment;
  programmatic: boolean;
  observer: ResizeObserver;
  closed: boolean;
}

const revealEffect = StateEffect.define<DecorationSet | null>();

/** Holds the range a diagnostic jump highlighted, so the mark survives edits and disappears on the next jump. */
const revealField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, transaction) {
    let current = transaction.docChanged
      ? decorations.map(transaction.changes)
      : decorations;

    for (const effect of transaction.effects) {
      if (effect.is(revealEffect)) {
        current = effect.value ?? Decoration.none;
      }
    }

    return current;
  },
  provide: (field) => EditorView.decorations.from(field),
});

const views = new WeakMap<HTMLElement, Held>();

/** Lets .NET confirm the bundle really loaded before it decides whether to render the textarea fallback. */
export function isAvailable(): boolean {
  return true;
}

function theme(): Extension {
  return EditorView.theme({
    '&': { height: '100%', backgroundColor: 'var(--al-editor-bg, canvas)', color: 'var(--al-editor-text, canvastext)' },
    '.cm-scroller': {
      fontFamily: 'var(--al-editor-font, ui-monospace, Consolas, monospace)',
      lineHeight: '1.45',
    },
    '.cm-gutters': {
      backgroundColor: 'var(--al-editor-gutter-bg, canvas)',
      color: 'var(--al-editor-gutter-text, graytext)',
      border: 'none',
      borderRight: '1px solid var(--al-editor-border, color-mix(in srgb, canvastext 20%, transparent))',
    },
    '.cm-activeLine': { backgroundColor: 'var(--al-editor-active-line, color-mix(in srgb, AccentColor 10%, transparent))' },
    '.cm-activeLineGutter': { backgroundColor: 'var(--al-editor-active-line, color-mix(in srgb, AccentColor 10%, transparent))' },
    '.cm-content': { caretColor: 'var(--al-editor-caret, AccentColor)' },
    '&.cm-focused': { outline: '2px solid Highlight', outlineOffset: '-2px' },
    '.cm-search': { backgroundColor: 'var(--al-editor-bg, canvas)', color: 'var(--al-editor-text, canvastext)' },
    '.al-editor-reveal': { backgroundColor: 'var(--al-editor-reveal, color-mix(in srgb, Mark 40%, transparent))' },
    '.al-editor-reveal--error': { outline: '1px solid color-mix(in srgb, Mark 80%, canvastext)' },
  });
}

function languageFor(language: string): Extension {
  if (language === 'yaml') {
    return yaml();
  }

  if (language === 'json') {
    return json();
  }

  return [];
}

function baseExtensions(options: EditorOptions): Extension[] {
  return [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightActiveLine(),
    history(),
    foldGutter(),
    drawSelection(),
    dropCursor(),
    rectangularSelection(),
    indentOnInput(),
    bracketMatching(),
    closeBrackets(),
    autocompletion(),
    highlightSelectionMatches(),
    EditorView.lineWrapping,
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    EditorView.contentAttributes.of({
      'aria-label': options.ariaLabel,
      'aria-multiline': 'true',
      spellcheck: 'false',
    }),
    keymap.of([
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...completionKeymap,
      indentWithTab,
    ]),
    languageFor(options.language),
    theme(),
    revealField,
  ];
}

/**
 * Creates the editor inside `element` and wires the change callback back to .NET.
 *
 * Returns false when CodeMirror cannot be constructed, which is the signal the .NET component uses to
 * render its documented textarea fallback rather than a blank panel.
 */
export function attach(element: HTMLElement, options: EditorOptions, dotNet: DotNetTarget | null): boolean {
  dispose(element);

  try {
    const language = new Compartment();
    // Assigned after the view exists; the update listener only fires on transactions, which cannot
    // happen before construction finishes, so the closure never sees it unassigned.
    let held: Held;

    const state = EditorState.create({
      doc: options.doc ?? '',
      extensions: [
        ...baseExtensions(options),
        language.of(languageFor(options.language)),
        EditorView.updateListener.of((update: ViewUpdate) => {
          if (update.docChanged && !held.programmatic && !held.closed && dotNet !== null && dotNet !== undefined) {
            // A late keystroke can reach .NET after it has begun disposing. Swallow the rejection
            // so it never becomes an unhandled promise error in the console — but do not close the
            // editor for it. A transient interop hiccup must be recoverable on the next keystroke;
            // only dispose() closes the editor for good.
            void dotNet.invokeMethodAsync('OnTextChanged', update.state.doc.toString())
              .catch(() => {
                // This one edit did not reach .NET; the next one is allowed to try again.
              });
          }
        }),
      ],
    });

    const view = new EditorView({ state, parent: element });
    const observer = new ResizeObserver(() => view.requestMeasure());
    observer.observe(element);
    held = { view, language, programmatic: false, observer, closed: false };
    views.set(element, held);
    return true;
  } catch {
    return false;
  }
}

/** Replaces the document without reporting a change, for "load example" and programmatic resets. */
export function setText(element: HTMLElement, text: string): boolean {
  const body = text ?? '';
  const held = views.get(element);
  if (held === undefined) {
    return false;
  }

  // A whole-document dispatch that happens to insert the identical text still
  // creates a history step and can move the selection. The .NET side is careful
  // not to echo its own reports back here; this guard keeps the editor safe if
  // it ever does, and avoids a pointless interop hop on a no-op replacement.
  if (held.view.state.doc.toString() === body) {
    return true;
  }

  held.programmatic = true;
  try {
    held.view.dispatch({
      changes: { from: 0, to: held.view.state.doc.length, insert: body },
    });
  } finally {
    held.programmatic = false;
  }

  return true;
}

export function getText(element: HTMLElement): string {
  return views.get(element)?.view.state.doc.toString() ?? '';
}

/**
 * Moves the cursor to a 1-based line/column, centres it, focuses the editor and marks the affected lines.
 *
 * Returns the position actually used, clamped into the document, so the caller can tell the reader when
 * the line it asked for is not there yet — which can happen while a debounced analysis is in flight.
 */
export function reveal(
  element: HTMLElement,
  line: number,
  column: number,
  endLine: number,
  endColumn: number,
  severity: string,
): { line: number; column: number } | null {
  const held = views.get(element);
  if (held === undefined) {
    return null;
  }

  const view = held.view;
  const doc = view.state.doc;
  const startLine = clamp(Math.trunc(line), 1, doc.lines);
  const finishLine = clamp(Math.trunc(endLine), startLine, doc.lines);
  const start = doc.line(startLine);
  const finish = doc.line(finishLine);
  const caretColumn = clamp(Math.trunc(column), 1, start.length + 1);
  const from = start.from;
  const to = Math.max(finish.to, start.to);
  const className = 'al-editor-reveal al-editor-reveal--' + (severity || 'info').toLowerCase();
  const marks = Decoration.set(
    [Decoration.mark({ class: className }).range(from, Math.max(to, from + 1))],
    true,
  );

  view.dispatch({
    selection: { anchor: from + caretColumn - 1, head: from + caretColumn - 1 },
    effects: [
      revealEffect.of(marks),
      EditorView.scrollIntoView(from, { y: 'center', yMargin: 24 }),
    ],
  });
  view.focus();
  return { line: startLine, column: caretColumn };
}

export function focus(element: HTMLElement): boolean {
  const held = views.get(element);
  if (held === undefined) {
    return false;
  }

  held.view.focus();
  return true;
}

/** Drops the mark a previous jump left behind, so a stale highlight cannot read as a current finding. */
export function clearReveal(element: HTMLElement): boolean {
  const held = views.get(element);
  if (held === undefined) {
    return false;
  }

  held.view.dispatch({ effects: revealEffect.of(Decoration.none) });
  return true;
}

export function dispose(element: HTMLElement): void {
  const held = views.get(element);
  if (held !== undefined) {
    // Mark first: destroy() can synchronously fire an update listener, and an
    // in-flight invokeMethodAsync must not call back into .NET after disposal.
    held.closed = true;
    held.observer.disconnect();
    held.view.destroy();
    views.delete(element);
  }
}

/**
 * Focuses the nth element matching `selector` inside `root`, clamped to what is there.
 *
 * ActionLens needs this for the diagnostics list's arrow keys: a Blazor `@ref` inside a loop cannot hold
 * one reference per row, so the shell keeps a single container reference and asks the DOM for the row.
 */
export function focusNth(root: HTMLElement, selector: string, index: number): boolean {
  const nodes = root.querySelectorAll<HTMLElement>(selector);
  if (nodes.length === 0) {
    return false;
  }

  const target = nodes[clamp(Math.trunc(index), 0, nodes.length - 1)];
  target.focus();
  return document.activeElement === target;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(Math.max(value, minimum), maximum);
}
