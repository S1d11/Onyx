// Compact, dependency-free markdown renderer for chat output.
// Supports: headings, bold/italic/strike/inline-code, fenced code blocks with
// language label + copy button, unordered/ordered lists, task lists, blockquotes,
// links, autolinks, hr, paragraphs, line breaks. Escapes HTML for safety.
(function () {
  function esc(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function inline(s) {
    s = esc(s);
    // inline code
    s = s.replace(/`([^`]+)`/g, (_, c) => '<code>' + c + '</code>');
    // bold
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    s = s.replace(/__([^_]+)__/g, '<strong>$1</strong>');
    // italic
    s = s.replace(/(^|[^*])\*([^*]+)\*/g, '$1<em>$2</em>');
    s = s.replace(/(^|[^_])_([^_]+)_/g, '$1<em>$2</em>');
    // strikethrough
    s = s.replace(/~~([^~]+)~~/g, '<del>$1</del>');
    // links [text](url)
    s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,
      '<a href="$2" target="_blank" rel="noopener">$1</a>');
    // autolinks
    s = s.replace(/(^|[\s(])((https?:\/\/)[^\s<)]+)/g, '$1<a href="$2" target="_blank" rel="noopener">$2</a>');
    return s;
  }

  function render(md) {
    if (!md) return "";
    var lines = md.replace(/\r\n/g, "\n").split("\n");
    var out = [];
    var i = 0;
    var codeLang = null, codeBuf = [];
    var listType = null, listItems = [];

    function flushList() {
      if (!listType) return;
      var tag = listType === "ol" ? "ol" : "ul";
      out.push("<" + tag + ">" + listItems.join("") + "</" + tag + ">");
      listType = null; listItems = [];
    }
    function flushPara(buf) {
      if (buf.length) out.push("<p>" + buf.join("<br>") + "</p>");
    }

    var para = [];
    function flushParaBuf() { flushPara(para); para = []; }

    while (i < lines.length) {
      var line = lines[i];

      // fenced code
      if (line.match(/^```/)) {
        if (codeLang === null) {
          flushList(); flushParaBuf();
          codeLang = line.replace(/^```/, "").trim() || "text";
          codeBuf = [];
        } else {
          var code = esc(codeBuf.join("\n"));
          out.push('<pre><div class="code-head"><span>' + esc(codeLang) + '</span>' +
            '<button class="copy-code" onclick="OllamaUI.copyCode(this)">Copy</button></div>' +
            '<code>' + code + '</code></pre>');
          codeLang = null; codeBuf = [];
        }
        i++; continue;
      }
      if (codeLang !== null) { codeBuf.push(line); i++; continue; }

      // blank line
      if (line.trim() === "") { flushList(); flushParaBuf(); i++; continue; }

      // heading
      var h = line.match(/^(#{1,6})\s+(.*)$/);
      if (h) { flushList(); flushParaBuf(); var lvl = h[1].length; out.push("<h" + lvl + ">" + inline(h[2]) + "</h" + lvl + ">"); i++; continue; }

      // hr
      if (line.match(/^(-{3,}|\*{3,}|_{3,})\s*$/)) { flushList(); flushParaBuf(); out.push("<hr>"); i++; continue; }

      // blockquote
      if (line.match(/^>\s?/)) { flushList(); flushParaBuf(); var q = []; while (i < lines.length && lines[i].match(/^>\s?/)) { q.push(lines[i].replace(/^>\s?/, "")); i++; } out.push("<blockquote>" + inline(q.join(" ")) + "</blockquote>"); continue; }

      // task list / unordered list
      var ul = line.match(/^(\s*)[-*+]\s+(.*)$/);
      var ol = line.match(/^(\s*)\d+\.\s+(.*)$/);
      if (ul) {
        flushParaBuf();
        if (listType && listType !== "ul") flushList();
        listType = "ul";
        var task = ul[2].match(/^\[( |x)\]\s+(.*)$/i);
        if (task) {
          var chk = task[1].toLowerCase() === "x" ? "checked" : "";
          listItems.push('<li style="list-style:none"><input type="checkbox" ' + chk + ' disabled> ' + inline(task[2]) + '</li>');
        } else {
          listItems.push("<li>" + inline(ul[2]) + "</li>");
        }
        i++; continue;
      }
      if (ol) {
        flushParaBuf();
        if (listType && listType !== "ol") flushList();
        listType = "ol";
        listItems.push("<li>" + inline(ol[2]) + "</li>");
        i++; continue;
      }

      // paragraph text
      flushList();
      para.push(inline(line));
      i++;
    }
    if (codeLang !== null) { // unterminated code block
      out.push('<pre><code>' + esc(codeBuf.join("\n")) + '</code></pre>');
    }
    flushList(); flushParaBuf();
    return out.join("");
  }

  window.OllamaMD = { render: render, escape: esc };
})();
