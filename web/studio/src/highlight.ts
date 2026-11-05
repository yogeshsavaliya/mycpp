const keywords = new Set([
  "public", "private", "protected", "internal", "static", "return", "if", "else", "while",
  "unchecked", "class", "new", "throw", "true", "false", "null", "int", "uint", "long",
  "ulong", "bool", "void", "string", "namespace", "using"
]);

export function highlight(code: string): string {
  const escaped = code.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  return escaped.replace(/(\/\/.*)|("(?:\\.|[^"\\])*")|\b([A-Za-z_][A-Za-z0-9_]*)\b/g, (match, comment: string | undefined, literal: string | undefined, word: string | undefined) => {
    if (comment)
      return `<span class="tok-c">${comment}</span>`;
    if (literal)
      return `<span class="tok-s">${literal}</span>`;
    if (word && keywords.has(word))
      return `<span class="tok-k">${word}</span>`;
    return match;
  });
}
