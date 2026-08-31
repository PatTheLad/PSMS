window.psmsIntelliSense = (function () {
  const keywords = [
    "SELECT","FROM","WHERE","JOIN","INNER","LEFT","RIGHT","FULL","OUTER","ON","AND","OR","NOT",
    "INSERT","INTO","VALUES","UPDATE","SET","DELETE","CREATE","ALTER","DROP","TABLE","VIEW",
    "PROCEDURE","FUNCTION","INDEX","ORDER","BY","GROUP","HAVING","AS","DISTINCT","TOP","UNION",
    "ALL","EXISTS","IN","BETWEEN","LIKE","IS","CASE","WHEN","THEN","ELSE","END","BEGIN",
    "COMMIT","ROLLBACK","WITH","COUNT","SUM","AVG","MIN","MAX","CAST","CONVERT","DECLARE",
    "EXEC","EXECUTE","GO","USE","SCHEMA","DATABASE","TRUNCATE","NULL"
  ];
  const keywordSet = {};
  keywords.forEach(function (k) { keywordSet[k.toLowerCase()] = true; });

  const state = {
    currentDatabase: "",
    databases: [],
    objects: [],
    columns: [],
    dbSet: {},
    tableSet: {},
    registered: false,
    editors: {}
  };

  function quote(name) {
    if (name == null || name === "") return "";
    if (/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) return name;
    return "[" + String(name).replace(/]/g, "]]") + "]";
  }

  function isDbo(schema) {
    return !schema || String(schema).toLowerCase() === "dbo";
  }

  function sameDb(db) {
    if (!db || !state.currentDatabase) return true;
    return String(db).toLowerCase() === String(state.currentDatabase).toLowerCase();
  }

  function objectInsertText(o) {
    if (o.kind === "Schema") return quote(o.name);

    var local = isDbo(o.schema) ? quote(o.name) : (quote(o.schema) + "." + quote(o.name));
    if (sameDb(o.database)) return local;

    // Cross-database: db..table (dbo) or db.schema.table
    if (isDbo(o.schema)) return quote(o.database) + ".." + quote(o.name);
    return quote(o.database) + "." + quote(o.schema) + "." + quote(o.name);
  }

  function rebuildLookup() {
    state.dbSet = {};
    state.tableSet = {};
    state.databases.forEach(function (d) {
      state.dbSet[String(d).toLowerCase()] = true;
    });
    state.objects.forEach(function (o) {
      if (o.kind === "Table" || o.kind === "View") {
        state.tableSet[String(o.name).toLowerCase()] = o.kind;
      }
    });
  }

  function expandKind(code) {
    switch (code) {
      case "S": case "Schema": return "Schema";
      case "T": case "Table": return "Table";
      case "V": case "View": return "View";
      case "P": case "Procedure": return "Procedure";
      case "F": case "Function": return "Function";
      case "C": case "Column": return "Column";
      default: return code || "Table";
    }
  }

  function normalizeObject(o) {
    return {
      schema: o.schema != null ? o.schema : (o.s || ""),
      name: o.name != null ? o.name : (o.n || ""),
      kind: expandKind(o.kind != null ? o.kind : o.k),
      database: o.database != null ? o.database : (o.d || null)
    };
  }

  function normalizeColumn(c) {
    return {
      schema: c.schema != null ? c.schema : (c.s || ""),
      table: c.table != null ? c.table : (c.t || ""),
      name: c.name != null ? c.name : (c.n || ""),
      dataType: c.dataType != null ? c.dataType : (c.ty || "")
    };
  }

  function setCatalog(payload) {
    if (!payload) {
      state.currentDatabase = "";
      state.databases = [];
      state.objects = [];
      state.columns = [];
    } else {
      state.currentDatabase = payload.currentDatabase || "";
      state.databases = Array.isArray(payload.databases) ? payload.databases : [];
      state.objects = Array.isArray(payload.objects) ? payload.objects.map(normalizeObject) : [];
      state.columns = Array.isArray(payload.columns) ? payload.columns.map(normalizeColumn) : [];
    }
    rebuildLookup();
    refreshAllHighlights();
  }

  function setCatalogJson(json) {
    if (!json || json === "null") {
      setCatalog(null);
      return;
    }
    try {
      setCatalog(JSON.parse(json));
    } catch (e) {
      // ignore malformed payload
    }
  }

  function matches(text, filter) {
    if (!filter) return true;
    return String(text).toLowerCase().indexOf(String(filter).toLowerCase()) >= 0;
  }

  function startsWithFilter(text, filter) {
    if (!filter) return true;
    return String(text).toLowerCase().indexOf(String(filter).toLowerCase()) === 0;
  }

  function kindToMonaco(kind) {
    const K = (window.monaco && monaco.languages && monaco.languages.CompletionItemKind) || {};
    switch (kind) {
      case "Database": return K.Folder || 19;
      case "Schema": return K.Module || 8;
      case "Table": return K.Class || 5;
      case "View": return K.Interface || 7;
      case "Procedure": return K.Method || 0;
      case "Function": return K.Function || 1;
      case "Column": return K.Field || 3;
      default: return K.Text || 18;
    }
  }

  function parseDotted(prefix) {
    const m = prefix.match(/([A-Za-z0-9_\]\.\[]+)$/);
    if (!m) return { afterDot: false, parts: [] };
    const token = m[1];
    const afterDot = token.endsWith(".");
    const body = afterDot ? token.slice(0, -1) : token;
    if (!afterDot && body.indexOf(".") < 0) return { afterDot: false, parts: [] };

    const parts = [];
    let i = 0;
    while (i < body.length) {
      if (body[i] === ".") {
        parts.push("");
        i++;
        continue;
      }
      if (body[i] === "[") {
        const end = body.indexOf("]", i);
        if (end < 0) break;
        parts.push(body.slice(i + 1, end));
        i = end + 1;
        if (body[i] === ".") i++;
        continue;
      }
      let j = i;
      while (j < body.length && /[A-Za-z0-9_]/.test(body[j])) j++;
      if (j === i) break;
      parts.push(body.slice(i, j));
      i = j;
      if (body[i] === ".") i++;
    }
    return { afterDot: afterDot, parts: parts };
  }

  function isKnownDatabase(name) {
    return !!state.dbSet[String(name).toLowerCase()];
  }

  function isKnownSchema(name) {
    const n = String(name).toLowerCase();
    return state.objects.some(function (o) {
      return o.kind === "Schema" && String(o.name).toLowerCase() === n;
    });
  }

  function rankKey(name, filter, tier) {
    const prefix = startsWithFilter(name, filter) ? "0" : "1";
    return tier + prefix + String(name).toLowerCase();
  }

  function pushObject(bucket, o, filter, range, insertText) {
    if (o.kind === "Schema" || o.kind === "Column") return;
    const label = o.name;
    const detailDb = o.database ? (" · " + o.database) : "";
    if (!matches(o.name, filter)
      && !matches(o.schema + "." + o.name, filter)
      && !matches((o.database || "") + "." + o.name, filter)) {
      return;
    }
    bucket.push({
      label: label,
      kind: kindToMonaco(o.kind),
      detail: o.kind + " · " + o.schema + detailDb,
      insertText: insertText,
      range: range,
      filterText: o.name + " " + o.schema + " " + (o.database || ""),
      sortText: rankKey(o.name, filter, sameDb(o.database) ? "2" : "4")
    });
  }

  function suggestObjectsInSchema(bucket, schemaName, filter, range, databaseHint) {
    const target = schemaName === "" ? "dbo" : schemaName;
    state.objects.forEach(function (o) {
      if (o.kind === "Schema" || o.kind === "Column") return;
      if (String(o.schema).toLowerCase() !== String(target).toLowerCase()) return;
      if (databaseHint && !sameDb(o.database) &&
          String(o.database).toLowerCase() !== String(databaseHint).toLowerCase()) {
        return;
      }
      // Qualifier already typed → insert name only
      pushObject(bucket, o, filter, range, quote(o.name));
    });
  }

  function suggestSchemas(bucket, filter, range) {
    state.objects.forEach(function (o) {
      if (o.kind !== "Schema") return;
      if (!matches(o.name, filter)) return;
      bucket.push({
        label: o.name,
        kind: kindToMonaco("Schema"),
        detail: "Schema",
        insertText: quote(o.name),
        range: range,
        filterText: o.name,
        sortText: rankKey(o.name, filter, "1")
      });
    });
  }

  function suggestColumns(bucket, schema, table, filter, range, tier) {
    tier = tier || "3";
    state.columns.forEach(function (c) {
      if (schema && String(c.schema).toLowerCase() !== String(schema).toLowerCase()) return;
      if (String(c.table).toLowerCase() !== String(table).toLowerCase()) return;
      if (!matches(c.name, filter)) return;
      bucket.push({
        label: c.name,
        kind: kindToMonaco("Column"),
        detail: "Column · " + c.dataType + " · " + c.schema + "." + c.table,
        insertText: quote(c.name),
        range: range,
        filterText: c.name,
        sortText: rankKey(c.name, filter, tier)
      });
    });
  }

  /** All columns matching filter (used when typing e.g. "Ar" → ArtNr). */
  function suggestAllColumns(bucket, filter, range, tier) {
    tier = tier || "3";
    state.columns.forEach(function (c) {
      if (!matches(c.name, filter)) return;
      bucket.push({
        label: c.name,
        kind: kindToMonaco("Column"),
        detail: "Column · " + c.dataType + " · " + c.schema + "." + c.table,
        insertText: quote(c.name),
        range: range,
        filterText: c.name + " " + c.schema + " " + c.table,
        sortText: rankKey(c.name, filter, tier)
      });
    });
  }

  /** Typing a table/database name after FROM / JOIN / INTO. */
  function isTableNameContext(prefix) {
    return /\b(FROM|INTO|UPDATE)\s+[A-Za-z0-9_\[\]"`.]*$/i.test(prefix)
      || /\b(FROM|INTO|UPDATE)\s*$/i.test(prefix)
      || /\bJOIN\s+[A-Za-z0-9_\[\]"`.]*$/i.test(prefix)
      || /\bJOIN\s*$/i.test(prefix)
      || /\b(INNER|LEFT|RIGHT|FULL|OUTER|CROSS)\s+JOIN\s+[A-Za-z0-9_\[\]"`.]*$/i.test(prefix)
      || /\b(INNER|LEFT|RIGHT|FULL|OUTER|CROSS)\s+JOIN\s*$/i.test(prefix);
  }

  /** SELECT list, JOIN ON, WHERE, ORDER BY, etc. — prefer columns over tables. */
  function isColumnContext(prefix) {
    if (isTableNameContext(prefix)) return false;
    const u = prefix.toUpperCase();
    if (/\bSELECT\b/.test(u)) {
      var fromPos = u.lastIndexOf(" FROM ");
      if (fromPos < 0) return true;
    }
    if (/\b(ON|WHERE|AND|OR|SET|HAVING|BY)\b/.test(u)) return true;
    if (/,\s*[A-Za-z0-9_\[\]"`.]*$/i.test(prefix)) return true;
    return false;
  }

  function mergeBudgets(kw, dbs, objs, cols, columnContext) {
    if (columnContext) {
      return kw.slice(0, 8)
        .concat(cols.slice(0, 220))
        .concat(objs.slice(0, 120))
        .concat(dbs.slice(0, 50))
        .slice(0, 400);
    }
    return kw.slice(0, 12)
      .concat(objs.slice(0, 200))
      .concat(cols.slice(0, 180))
      .concat(dbs.slice(0, 80))
      .slice(0, 400);
  }

  function provideCompletionItems(model, position) {
    const line = model.getLineContent(position.lineNumber);
    const prefix = line.substring(0, Math.max(0, position.column - 1));
    const word = model.getWordUntilPosition(position);
    const filter = word.word || "";
    const range = {
      startLineNumber: position.lineNumber,
      endLineNumber: position.lineNumber,
      startColumn: word.startColumn,
      endColumn: word.endColumn
    };

    const kw = [];
    const dbs = [];
    const objs = [];
    const cols = [];
    const dotted = parseDotted(prefix);
    const columnCtx = isColumnContext(prefix);
    const colTier = columnCtx ? "2" : "3";

    if (dotted.afterDot) {
      if (dotted.parts.length === 1) {
        const q = dotted.parts[0];
        const asDb = isKnownDatabase(q);
        const asSchema = isKnownSchema(q) || isDbo(q);

        if (asDb) {
          suggestSchemas(objs, filter, range);
          suggestObjectsInSchema(objs, "dbo", filter, range, q);
        }
        if (asSchema || !asDb) {
          suggestObjectsInSchema(objs, q, filter, range, state.currentDatabase);
        }
        suggestColumns(cols, null, q, filter, range, colTier);
      } else if (dotted.parts.length === 2) {
        const a = dotted.parts[0];
        const b = dotted.parts[1];
        if (b === "") {
          suggestObjectsInSchema(objs, "dbo", filter, range, a);
        } else if (isKnownDatabase(a)) {
          suggestObjectsInSchema(objs, b, filter, range, a);
        } else {
          suggestColumns(cols, a, b, filter, range, colTier);
        }
      } else if (dotted.parts.length >= 3) {
        const schema = dotted.parts[dotted.parts.length - 2];
        const table = dotted.parts[dotted.parts.length - 1];
        suggestColumns(cols, schema === "" ? "dbo" : schema, table, filter, range, colTier);
      }
    } else if (filter.length >= 1) {
      keywords.forEach(function (k) {
        if (!matches(k, filter)) return;
        kw.push({
          label: k,
          kind: (window.monaco && monaco.languages.CompletionItemKind.Keyword) || 17,
          detail: "keyword",
          insertText: k,
          range: range,
          filterText: k,
          sortText: rankKey(k, filter, "0")
        });
      });

      if (!isTableNameContext(prefix)) {
        suggestAllColumns(cols, filter, range, colTier);
      }

      state.databases.forEach(function (db) {
        if (!matches(db, filter)) return;
        dbs.push({
          label: db,
          kind: kindToMonaco("Database"),
          detail: "Database",
          insertText: quote(db),
          range: range,
          filterText: db,
          sortText: rankKey(db, filter, "1")
        });
      });

      state.objects.forEach(function (o) {
        if (o.kind === "Column") return;
        if (o.kind === "Schema") {
          if (!matches(o.name, filter)) return;
          objs.push({
            label: o.name,
            kind: kindToMonaco("Schema"),
            detail: "Schema",
            insertText: quote(o.name),
            range: range,
            filterText: o.name,
            sortText: rankKey(o.name, filter, "1")
          });
          return;
        }
        pushObject(objs, o, filter, range, objectInsertText(o));
      });
    }

    return { suggestions: mergeBudgets(kw, dbs, objs, cols, columnCtx) };
  }

  function findIdentifierRanges(model) {
    const ranges = [];
    const lineCount = model.getLineCount();
    const re = /\[([^\]]+)\]|[A-Za-z_][A-Za-z0-9_]*/g;

    for (let line = 1; line <= lineCount; line++) {
      const text = model.getLineContent(line);
      let m;
      re.lastIndex = 0;
      while ((m = re.exec(text))) {
        const raw = m[1] != null ? m[1] : m[0];
        const start = m.index + 1;
        const end = start + m[0].length;
        const key = String(raw).toLowerCase();
        if (keywordSet[key]) continue;

        let cls = null;
        if (state.tableSet[key]) cls = "psms-hl-table";
        else if (state.dbSet[key]) cls = "psms-hl-db";

        if (cls) {
          ranges.push({
            range: {
              startLineNumber: line,
              startColumn: start,
              endLineNumber: line,
              endColumn: end
            },
            options: {
              inlineClassName: cls,
              stickiness: 1
            }
          });
        }
      }
    }
    return ranges;
  }

  function refreshHighlights(editorId) {
    const entry = state.editors[editorId];
    if (!entry || !entry.editor) return;
    try {
      const model = entry.editor.getModel();
      if (!model) return;
      const decos = findIdentifierRanges(model);
      entry.deco = entry.editor.deltaDecorations(entry.deco || [], decos);
    } catch (e) { /* */ }
  }

  function refreshAllHighlights() {
    Object.keys(state.editors).forEach(refreshHighlights);
  }

  function attachEditor(editorId) {
    try {
      if (!window.blazorMonaco || !blazorMonaco.editor) return false;
      const editor = blazorMonaco.editor.getEditor(editorId, true);
      if (!editor) return false;

      const prev = state.editors[editorId];
      if (prev && prev.dispose) {
        try { prev.dispose.dispose(); } catch (e) { /* */ }
      }

      const entry = { editor: editor, deco: [], timer: null, dispose: null };
      state.editors[editorId] = entry;

      entry.dispose = editor.onDidChangeModelContent(function () {
        if (entry.timer) clearTimeout(entry.timer);
        entry.timer = setTimeout(function () { refreshHighlights(editorId); }, 120);
      });

      refreshHighlights(editorId);
      return true;
    } catch (e) {
      return false;
    }
  }

  function ensureRegistered() {
    try {
      if (state.registered) return true;
      if (!window.monaco || !monaco.languages || !monaco.languages.registerCompletionItemProvider) {
        return false;
      }
      monaco.languages.registerCompletionItemProvider("sql", {
        triggerCharacters: [".", "[", "@"],
        provideCompletionItems: provideCompletionItems
      });
      state.registered = true;
      return true;
    } catch (e) {
      return false;
    }
  }

  return {
    setCatalog: setCatalog,
    setCatalogJson: setCatalogJson,
    ensureRegistered: ensureRegistered,
    attachEditor: attachEditor,
    refreshHighlights: refreshAllHighlights,
    getCount: function () {
      return state.databases.length + state.objects.length + state.columns.length;
    }
  };
})();
