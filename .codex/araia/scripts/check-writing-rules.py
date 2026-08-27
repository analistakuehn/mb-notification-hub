#!/usr/bin/env python3
"""
Writing-rules linter for framework artifacts.

Loads the appropriate rule set (PT-BR or EN) and runs deterministic regex
checks against a markdown artifact, skipping fenced code blocks and inline
code spans. Emits one line per finding plus an exit code:

  0  no findings
  1  warnings only (soft-fail)
  2  errors (hard-fail)

This script implements ONLY the high-confidence rules. Style/voice rules
that need semantic interpretation stay with the `artifact-writer` skill.

Two input modes, auto-detected by extension (override with --mode):

  - markdown : prose artifacts (.md/.mdx/...). Whole-line text is checked;
               fenced code blocks and inline backtick spans are stripped.
  - source   : code files (.dart/.ts/.py/...). ONLY natural-language text is
               checked: line and block comments, doc comments, and string
               literals. Identifiers, keywords, and operators are never read,
               so a class named `Configuracao` or a variable `nao` is ignored
               while a comment "verificacao da sessao" is flagged. This is the
               code-embedded-text track of post-write-language-enforcement.md.

Used by `.github/workflows/check-writing-rules.yml` and by the PostToolUse
language hook (`framework/hooks/post-write-language-check.mjs`), which calls
this script in --source mode to turn its nudge into a concrete, line-anchored
finding list. Initial CI deployment is warn-only (exit 0 even on findings);
flip the `--strict` flag when the contributor base has cleaned existing
artifacts.
"""

from __future__ import annotations

import argparse
import re
import sys
from functools import lru_cache
from pathlib import Path
from typing import Iterable

# The findings carry accented PT-BR suggestions ("sessão"). On Windows the
# default console encoding is cp1252, which would mojibake them. Force UTF-8 on
# both streams so the hook (and any caller) receives readable diacritics.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass


PT_BR_FILLER_UNACCENTED = {
    "acao": "ação",
    "funcao": "função",
    "operacao": "operação",
    "configuracao": "configuração",
    "validacao": "validação",
    "integracao": "integração",
    "autenticacao": "autenticação",
    "autorizacao": "autorização",
    "execucao": "execução",
    "implementacao": "implementação",
    "definicao": "definição",
    "descricao": "descrição",
    "correcao": "correção",
    "excecao": "exceção",
    "conexao": "conexão",
    "remocao": "remoção",
    "decisao": "decisão",
    "manutencao": "manutenção",
    "transacao": "transação",
    "sessao": "sessão",
    "versao": "versão",
    "revisao": "revisão",
    "alteracao": "alteração",
    "reducao": "redução",
    "padrao": "padrão",
    "razao": "razão",
    "opcao": "opção",
    "nao": "não",
    "sao": "são",
    "esta": "está",
    "ja": "já",
    "so": "só",
    "ate": "até",
    "tres": "três",
    "apos": "após",
    "analise": "análise",
    "metrica": "métrica",
    "logica": "lógica",
    "metodo": "método",
    "criterio": "critério",
    "historico": "histórico",
    "usuario": "usuário",
    "preco": "preço",
    "servico": "serviço",
    "espaco": "espaço",
    "exercicio": "exercício",
    "inicio": "início",
    "serie": "série",
    "hipotese": "hipótese",
    "caracteristica": "característica",
    "codigo": "código",
    "numero": "número",
    "unico": "único",
    "proximo": "próximo",
    "proprio": "próprio",
    # --- expanded high-frequency technical PT-BR (the allowlist is a floor, not
    # the whole rule: the -cao/-sao suffix heuristic below catches the long tail
    # of -ção/-são words that are not enumerated here) ---
    "instalacao": "instalação",
    "compilacao": "compilação",
    "atualizacao": "atualização",
    "migracao": "migração",
    "publicacao": "publicação",
    "aplicacao": "aplicação",
    "informacao": "informação",
    "documentacao": "documentação",
    "renderizacao": "renderização",
    "navegacao": "navegação",
    "inicializacao": "inicialização",
    "verificacao": "verificação",
    "notificacao": "notificação",
    "autorizacao": "autorização",
    "criacao": "criação",
    "geracao": "geração",
    "iteracao": "iteração",
    "duracao": "duração",
    "anotacao": "anotação",
    "associacao": "associação",
    "condicao": "condição",
    "posicao": "posição",
    "transicao": "transição",
    "permissao": "permissão",
    "expressao": "expressão",
    "extensao": "extensão",
    "inclusao": "inclusão",
    "exclusao": "exclusão",
    "conclusao": "conclusão",
    "dimensao": "dimensão",
    "colisao": "colisão",
    "previsao": "previsão",
    "submissao": "submissão",
    "regressao": "regressão",
    "compreensao": "compreensão",
    "presenca": "presença",
    "ausencia": "ausência",
    "referencia": "referência",
    "dependencia": "dependência",
    "consequencia": "consequência",
    "experiencia": "experiência",
    "ocorrencia": "ocorrência",
    "diferenca": "diferença",
    "sequencia": "sequência",
    "frequencia": "frequência",
    "persistencia": "persistência",
    "instancia": "instância",
    "distancia": "distância",
    "importancia": "importância",
    "tolerancia": "tolerância",
    "parametro": "parâmetro",
    "dinamico": "dinâmico",
    "estatico": "estático",
    "semantico": "semântico",
    "sintatico": "sintático",
    "automatico": "automático",
    "generico": "genérico",
    "publico": "público",
    "atomico": "atômico",
    "anonimo": "anônimo",
    "sincrono": "síncrono",
    "assincrono": "assíncrono",
    "valido": "válido",
    "invalido": "inválido",
    "obvio": "óbvio",
    "minimo": "mínimo",
    "maximo": "máximo",
    "otimo": "ótimo",
    "ultimo": "último",
    "pagina": "página",
    "maquina": "máquina",
    "binario": "binário",
    "necessario": "necessário",
    "obrigatorio": "obrigatório",
    "diretorio": "diretório",
    "repositorio": "repositório",
    "cenario": "cenário",
    "fallback": "fallback",  # identity: kept so an EN term is never mis-suggested
    "comentario": "comentário",
    "formulario": "formulário",
    "relatorio": "relatório",
    "memoria": "memória",
    "categoria": "categoria",  # no accent: guards the heuristic from over-firing
    "canonico": "canônico",
    "economico": "econômico",
    "tecnica": "técnica",
    "tecnico": "técnico",
    "pratica": "prática",
    "pratico": "prático",
    "basico": "básico",
    "fisico": "físico",
    "grafico": "gráfico",
    "trafego": "tráfego",
    "nivel": "nível",
    "disponivel": "disponível",
    "possivel": "possível",
    "impossivel": "impossível",
    "responsavel": "responsável",
    "variavel": "variável",
    "imutavel": "imutável",
    "compativel": "compatível",
    "legivel": "legível",
    "util": "útil",
    "modulo": "módulo",
    "calculo": "cálculo",
    "vinculo": "vínculo",
    "titulo": "título",
    "multiplo": "múltiplo",
    "veiculo": "veículo",
    "ciclo": "ciclo",  # no accent: heuristic guard
    "porem": "porém",
    "tambem": "também",
    "alem": "além",
    "ninguem": "ninguém",
    "voce": "você",
    "esta": "está",
    "estao": "estão",
    "sera": "será",
    "serao": "serão",
    "ira": "irá",
    "havera": "haverá",
    "ate": "até",
    "apos": "após",
    "atraves": "através",
    "area": "área",
    "ideia": "ideia",  # no accent post-AO1990: heuristic guard
    "saida": "saída",
    "entrada": "entrada",  # guard
    "duvida": "dúvida",
    "padroes": "padrões",
    "versoes": "versões",
    "opcoes": "opções",
    "funcoes": "funções",
    "condicoes": "condições",
    "permissoes": "permissões",
    "excecoes": "exceções",
    "extensoes": "extensões",
}

# Dictionary entries that are ALSO common English words, or common identifiers
# named inside prose. Correcting them is safe only when the surrounding fragment
# is demonstrably PT-BR; applied blindly they corrupt English prose ("Recognized
# so they are" -> "Recognized só they are") or misname a module a comment refers
# to ("dispatch with node:util parseArgs" -> "node:útil").
PT_AMBIGUOUS_EN_TOKENS = {"so", "area", "ate", "ira", "util"}

# Tokens whose bare form is ALSO a correct PT-BR word, distinguished only by
# what follows. "esta" is the feminine demonstrative in "esta forma" / "esta
# secao" and the verb only in "esta em" / "esta pronto". Flagging it
# unconditionally fires on correct prose, and a rule that cries wolf trains the
# team to route around the gate. Each entry pins the right-hand context that
# makes the accented form the only valid reading.
_PT_VERB_COMPLEMENT = (
    r"(?:em|na|no|nas|nos|ao|aos|à|às|sob|sobre|entre|fora|acima|abaixo|dentro|"
    r"sendo|disponivel|disponível|correto|correta|pronto|pronta|ativo|ativa|"
    r"vazio|vazia|presente|ausente|previsto|prevista|"
    r"\w+(?:ado|ada|ados|adas|ido|ida|idos|idas|avel|ável|ivel|ível))\b"
)
PT_BR_CONTEXT_REQUIRED = {
    "esta": re.compile(rf"\besta\s+{_PT_VERB_COMPLEMENT}"),
}

# Tokens whose bare form is a correct third-person verb and whose accented form
# is a different word (the noun). "referencia" is the verb in "cada linha
# referencia um projeto" and "referência" the noun in "de referência";
# "sequencia" likewise against "sequência". The reading depends on the subject
# to the LEFT, which no right-hand context regex can settle, so applying the
# accent mechanically turns correct Portuguese into a grammatical error inside a
# durable artifact. These are therefore never auto-fixed and are reported as a
# warning for a human to resolve, the same restraint the machine-token rule
# applies to `node:util`.
PT_VERB_AMBIGUOUS_TOKENS = {"referencia", "sequencia"}

_PT_ACCENT_CHARS = re.compile(r"[áàâãéêíóôõúüç]")


def _has_ptbr_evidence(lower: str) -> bool:
    """True when a fragment carries PT-BR evidence beyond the ambiguous tokens.

    Evidence: an accented character, an unambiguous dictionary hit, or a
    -ção/-são-family suffix hit. Gates both the findings and the auto-fix for
    `PT_AMBIGUOUS_EN_TOKENS`, so an English fragment containing only "so" or
    "area" is never flagged or rewritten.
    """
    if _PT_ACCENT_CHARS.search(lower):
        return True
    for bare, accented in PT_BR_FILLER_UNACCENTED.items():
        if bare == accented or bare in PT_AMBIGUOUS_EN_TOKENS:
            continue
        if re.search(rf"\b{re.escape(bare)}\b", lower):
            return True
    for m in re.finditer(r"\b[a-z]{4,}\b", lower):
        if _suffix_suggestion(m.group(0)):
            return True
    return False

# Suffix heuristic: words ending in these unaccented forms almost always take the
# accented ending in standard PT-BR (the -ção/-são family is near-exceptionless).
# This is what makes the check generic instead of allowlist-bounded. Each entry
# maps an unaccented suffix to its accented form; the suggestion is computed by
# swapping the suffix. A short exception set guards the handful of words that end
# the same way without an accent, plus EN technical terms.
PT_BR_SUFFIX_RULES = [
    ("coes", "ções"),
    ("soes", "sões"),
    ("cao", "ção"),
    ("sao", "são"),
]
# Words/fragments that END in a heuristic suffix but must NOT be flagged: short
# native words handled exactly above, and any English technical term that
# happens to collide. Matched against the whole lowercased token.
PT_BR_SUFFIX_EXCEPTIONS = {
    # short native -ao words that are not -ção/-são (handled in the map or correct)
    "mao", "pao", "chao", "irmao", "cidadao", "grao", "orgao", "sotao",
    # already-correct or EN tokens ending in the letters
    "sao",  # only as the standalone word, which the map already covers
}

EN_FORBIDDEN_PHRASES = [
    r"\bIt is worth noting that\b",
    r"\bPlease note that\b",
    r"\bIt is important to\b",
    r"\bIn other words\b",
    r"\bAs mentioned above\b",
    r"\bAs you can see\b",
    r"\bIt should be noted that\b",
    r"\bIt is recommended that\b",
    r"\bThere is a need for\b",
    r"\bIn the event that\b",
    r"\bleverage\b",
]

EN_FILLER_WORDS = [
    r"\bbasically\b",
    r"\bessentially\b",
    r"\beffectively\b",
    r"\bsimply\b",
]

# High-confidence language-consistency markers for Markdown narrative surfaces.
# These are not a general translation dictionary. They detect the failure mode
# where a template heading, metadata label, or whole prose line remains in the
# opposite language after the artifact language has been resolved.
EN_HEADING_MARKERS = {
    "acceptance", "alternatives", "approval", "architectural", "architecture",
    "artifacts", "boundaries", "business", "calculation", "candidate", "change",
    "checklist", "compatibility", "components", "concurrency", "consequences",
    "constraints", "context", "contracts", "coordination", "correction", "costs",
    "criteria", "critical", "current", "decision", "decisions", "derived",
    "drivers", "event", "evidence", "execution", "executive", "expected", "final",
    "flow", "follow", "future", "glossary", "goals", "historical", "history",
    "idempotency", "impact", "implementation", "incomplete", "level", "lifecycle",
    "message", "methodology", "metrics", "migration", "minimum", "model", "negative",
    "new", "non", "objectives", "objections", "observability", "operational",
    "organization", "organizational", "persistence", "plan", "policy", "portfolio",
    "positive", "privacy", "problem", "proposal", "proposed", "public",
    "recommendation", "reconciliation", "record", "recovery", "references",
    "reprocessing", "resilience", "resolution", "resolutions", "responsibilities",
    "review", "risks", "rules", "runbooks", "security", "service", "state", "states",
    "strategy", "success", "suggested", "summary", "support", "test", "trade",
    "unavailable", "up", "validation", "verification", "versioning",
}

PT_HEADING_MARKERS = {
    "aceite", "alterações", "alternativas", "aprovação", "arquitetura",
    "arquiteturais", "atual", "cálculo", "candidatas", "componentes",
    "compatibilidade", "concorrência", "consequências", "consideradas",
    "contexto", "contratos", "coordenação", "correção", "custos", "critérios",
    "decisão", "decisões", "derivadas", "direcionadores", "disponibilidade",
    "direcionadores", "evidências", "execução", "executivo", "esperados",
    "estratégia", "final", "fluxo",
    "futuro", "glossário", "histórico", "idempotência", "impacto",
    "implementação", "indisponíveis", "mensagens", "metodologia", "métricas",
    "migração", "modelo", "negativas", "objetivos", "objeções", "observabilidade",
    "operacional", "organização", "persistência", "plano", "política", "positivas",
    "privacidade", "problema", "proposta", "públicos", "recomendação",
    "reconciliação", "recuperação", "referências", "regras", "registro",
    "reprocessamento", "resiliência", "resolução", "responsabilidades", "revisão",
    "riscos", "segurança", "serviço", "sumário", "suporte", "validação",
    "verificação", "versionamento",
}

EN_SINGLETON_HEADINGS = {
    "compatibility", "context", "decision", "glossary", "history", "idempotency",
    "migration", "observability", "reconciliation", "references", "resolution",
    "risks",
}

PT_SINGLETON_HEADINGS = {
    "compatibilidade", "contexto", "decisão", "glossário", "histórico",
    "idempotência", "migração", "observabilidade", "reconciliação", "referências",
    "resolução", "riscos",
}

# Bare "status" is deliberately absent: it is an established loanword in
# Brazilian Portuguese technical writing, so a `| Status |` column in a PT-BR
# table is correct PT-BR, not leftover English. The qualified labels below still
# catch a genuinely English header.
EN_TABLE_LABELS = {
    "affected contexts and systems", "approver", "audience", "author",
    "author / owner", "change", "created", "date", "decision date",
    "decision scope", "expected reviewers", "field", "last updated", "outcome",
    "owner", "owner / decision makers", "primary rfc", "review gate", "reviewers",
    "scope", "source material / upstream intent", "trigger", "value",
}

# Bare "data" is deliberately absent: it is a Portuguese date label AND an
# ordinary English noun, so in an EN document a `| Data |` column (a data layer,
# a data source) is the far likelier reading. Keeping it here turned every EN
# table with a Data column red, and a rule that cries wolf is worse than no
# rule. The qualified PT date labels below still catch a real PT header.
PT_TABLE_LABELS = {
    "alcance", "alteração", "aprovador", "autor", "autor / responsável",
    "campo", "contextos e sistemas afetados", "data da decisão",
    "data de criação", "decisores", "escopo", "fontes / intenção de origem",
    "gatilho", "participantes", "responsável", "resultado", "revisão prevista",
    "revisores", "última atualização", "valor",
}

# "no" and "not" are listed so the shared-token rule below neutralizes them:
# "no" is also a Portuguese contraction, and an English sentence that repeats it
# ("leave no X, no Y, and no Z") was scoring as Portuguese.
EN_STOPWORDS = {
    "a", "an", "and", "are", "as", "at", "be", "before", "by", "for", "from",
    "if", "in", "into", "is", "it", "no", "not", "of", "on", "only", "or",
    "that", "the", "their", "this", "to", "when", "which", "with",
}

PT_STOPWORDS = {
    "a", "ao", "aos", "após", "as", "antes", "como", "com", "da", "das", "de",
    "do", "dos", "e", "em", "é", "esta", "este", "isso", "na", "nas", "não",
    "no", "nos", "o", "os", "ou", "para", "pela", "pelo", "por", "que", "se",
    "ser", "são", "um", "uma", "quando",
}

# Literal dash glyphs forbidden as punctuation in either language. The framework
# writes parentheticals with commas/parentheses and strong breaks with a period,
# colon, or semicolon (PT-BR keeps an em dash only to open dialogue). Inline code
# and fenced blocks are stripped before this runs, so rule files that NAME the
# glyph do not self-flag.
DASH_GLYPHS = ("—", "–", "―")  # em dash, en dash, horizontal bar


class Finding:
    def __init__(
        self,
        file: Path,
        line: int,
        severity: str,
        message: str,
        bare: str | None = None,
        suggestion: str | None = None,
    ) -> None:
        self.file = file
        self.line = line
        self.severity = severity
        self.message = message
        # For high-confidence diacritic findings, `bare` is the unaccented token
        # and `suggestion` its accented form, so --fix can apply the correction
        # by a word-boundary replace. None for findings that are not safely
        # auto-fixable (dash glyphs, EN phrases).
        self.bare = bare
        self.suggestion = suggestion

    def format_github(self) -> str:
        cmd = "::error" if self.severity == "error" else "::warning"
        return f"{cmd} file={self.file},line={self.line}::{self.message}"

    def __str__(self) -> str:
        return f"{self.file}:{self.line} [{self.severity}] {self.message}"


# A bare filesystem path is an identifier the author does not own, so demanding
# diacritics inside it reports a slug nobody can fix. Markdown link destinations
# are already masked above, but a path written outside link syntax is not: the
# common case is a `sources:` entry in YAML frontmatter. Both alternatives below
# require an extension, so ordinary prose with a slash ("e/ou") stays inspected.
_PATH_WITH_SEPARATOR = r"\S*[/\\]\S*\.[A-Za-z0-9]{1,6}\b"
_BARE_FILENAME = (
    r"\b[\w.-]+\.(?:md|mdx|markdown|txt|rst|adoc|py|mjs|cjs|js|ts|tsx|json"
    r"|jsonl|ya?ml|toml|svg|sh|ps1|sql|lock)\b"
)
_PATH_PATTERN = re.compile(f"{_PATH_WITH_SEPARATOR}|{_BARE_FILENAME}")


def _mask_paths(text: str) -> str:
    """Blank filesystem paths and bare filenames so they escape prose checks."""
    return _PATH_PATTERN.sub("", text)


def strip_code(text: str) -> list[tuple[int, str]]:
    """Return a list of (line_number, line_text) with code removed.

    - Lines inside fenced code blocks (```) are dropped.
    - Inline backtick spans on a line are replaced with empty strings.
    - `{{placeholder}}` template tokens are replaced with empty strings: they
      are identifiers a generator substitutes, not prose, so demanding
      diacritics inside them reports a slug the author cannot accent.
    - Markdown link and image destinations are replaced with empty strings for
      the same reason: a destination is a path or URL the filesystem or server
      owns, so `](.$plano-evolucao-x.md)` is not an unaccented word the author
      can fix. The link text and any quoted title stay under inspection.
    """
    lines = text.splitlines()
    in_fence = False
    out: list[tuple[int, str]] = []
    for idx, line in enumerate(lines, start=1):
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        cleaned = re.sub(r"`[^`]*`", "", line)
        cleaned = re.sub(r"\{\{[^{}]*\}\}", "", cleaned)
        cleaned = re.sub(r"(?<=\]\()[^()\s]*", "", cleaned)
        cleaned = _mask_paths(cleaned)
        out.append((idx, cleaned))
    return out


SOURCE_EXTENSIONS = {
    "dart", "ts", "tsx", "js", "jsx", "mjs", "cjs", "py", "go", "java", "kt",
    "kts", "swift", "rb", "php", "rs", "scala", "cs", "cpp", "cc", "c", "h",
    "hpp", "m", "mm", "vue", "svelte",
}
MARKDOWN_EXTENSIONS = {"md", "mdx", "markdown", "txt", "rst", "adoc"}


_REGEX_PRECEDERS = set("(,=:[!&|?{};+-*%<>~^\n")
_REGEX_KEYWORDS = {
    "return", "typeof", "case", "in", "of", "new", "delete", "void",
    "instanceof", "do", "else", "yield", "await",
}


def _opens_regex(text: str, i: int) -> bool:
    """Decide whether the ``/`` at ``i`` opens a regex literal or divides.

    A ``/`` starts a regex only where an expression may start, so the preceding
    non-space token decides. Without this the scanner desynchronises on any
    pattern holding a quote or a back quote (``/^```/``, ``/['\"]/``): the
    unbalanced quote opens a phantom string and swallows real code as prose.
    """
    k = i - 1
    while k >= 0 and text[k] in " \t":
        k -= 1
    if k < 0:
        return True
    if text[k] in _REGEX_PRECEDERS:
        return True
    word_end = k + 1
    while k >= 0 and (text[k].isalpha() or text[k] == "_"):
        k -= 1
    return text[k + 1 : word_end] in _REGEX_KEYWORDS


def _skip_regex_literal(text: str, i: int) -> int | None:
    """Return the offset just past a regex literal opening at ``i``, else None.

    A regex cannot span lines, so hitting a newline means the ``/`` was
    division after all and the caller should fall through. Character classes
    are tracked because they may hold an unescaped ``/``.
    """
    n = len(text)
    j = i + 1
    in_class = False
    while j < n:
        ch = text[j]
        if ch == "\\":
            j += 2
            continue
        if ch == "\n":
            return None
        if ch == "[":
            in_class = True
        elif ch == "]":
            in_class = False
        elif ch == "/" and not in_class:
            return j + 1
        j += 1
    return None


def _scan_template_literal(
    text: str, start: int, start_line: int
) -> tuple[int, list[tuple[int, int, int]]]:
    """Scan a JS/TS template literal from its opening back quote.

    Returns ``(end_offset, chunks)``, where each chunk is a
    ``(line_number, start, end)`` prose region. A newline does NOT terminate a
    template literal, so scanning stops at the closing back quote instead of at
    the first line break; truncating there hid every continuation line from the
    checker and from ``--fix``.

    Two rules keep the chunks safe to read and to rewrite:
      - one chunk per physical line, so a finding reports the line it is on
        rather than the line where the literal opened;
      - ``${...}`` interpolations are excluded, because their contents are
        expressions. Rewriting inside one produces code that no longer runs,
        the same hazard the machine-token rule guards against elsewhere.
    """
    n = len(text)
    i = start + 1
    line = start_line
    chunks: list[tuple[int, int, int]] = []
    seg_start = i
    seg_line = line

    def flush(end: int) -> None:
        if end > seg_start:
            chunks.append((seg_line, seg_start, end))

    while i < n:
        ch = text[i]
        if ch == "\\":
            i += 2
            continue
        if ch == "`":
            flush(i)
            return i + 1, chunks
        if ch == "\n":
            flush(i)
            line += 1
            i += 1
            seg_start = i
            seg_line = line
            continue
        if ch == "$" and i + 1 < n and text[i + 1] == "{":
            flush(i)
            depth = 1
            i += 2
            while i < n and depth:
                c = text[i]
                if c == "\\":
                    i += 2
                    continue
                if c == "`":
                    # A nested template literal inside the interpolation still
                    # holds prose; scan it rather than losing it.
                    j, nested = _scan_template_literal(text, i, line)
                    chunks.extend(nested)
                    line += text[i:j].count("\n")
                    i = j
                    continue
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                elif c == "\n":
                    line += 1
                i += 1
            seg_start = i
            seg_line = line
            continue
        i += 1

    # Unterminated: the opening back quote was not a template literal (a stray
    # quote in data, or a construct this scanner does not model). Yield nothing
    # rather than claiming the rest of the file is prose.
    return start + 1, []


def _extract_embedded_spans(text: str) -> list[tuple[int, int, int]]:
    """Return (line_number, start_offset, end_offset) for each prose fragment.

    Single character scanner shared by `extract_embedded_text` (which wants the
    fragment strings) and `fix_source_text` (which wants the offsets to rewrite
    in place). Extracts ONLY the spans a human writes in prose: line comments
    (``//``, ``#``), block comments (``/* */``), doc comments (``///``,
    ``/** */``), and string literals (single, double, triple, and back quoted).
    Everything else (identifiers, keywords, operators) is left out, so neither
    the diacritic check nor the fixer ever reads or rewrites a symbol name. The
    scanner is language-agnostic across the C/JS/Dart/Python family: it tracks
    string and comment state so a ``//`` inside a string, or a quote inside a
    comment, is not misread. Each span keeps the line number where it STARTS.

    A string literal with no internal whitespace is a machine token, not prose:
    a module specifier (``'node:util'``), a path (``'./lib/paths.mjs'``), an
    encoding (``'utf-8'``), a flag, or a key. Those are excluded, because
    rewriting one produces code that no longer runs (``node:util`` became
    ``node:útil`` once, and the CLI stopped importing). Comments keep no such
    restriction: a one-word comment is still prose.
    """
    spans: list[tuple[int, int, int]] = []

    def add_string_span(ln: int, start: int, end: int) -> None:
        inner = text[start:end].strip("\"'`")
        if not any(c.isspace() for c in inner):
            return
        spans.append((ln, start, end))

    i = 0
    n = len(text)
    line = 1
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        # Line comment: // (and dart ///), or # (python/ruby/shell-style)
        if (ch == "/" and nxt == "/") or ch == "#":
            j = text.find("\n", i)
            if j == -1:
                j = n
            spans.append((line, i, j))
            i = j
            continue
        # Block comment: /* ... */ (covers /** dartdoc/JSDoc too)
        if ch == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            j = n if j == -1 else j + 2
            spans.append((line, i, j))
            line += text[i:j].count("\n")
            i = j
            continue
        # Regex literal: skipped wholesale, before any quote is interpreted, so
        # a quote or back quote inside a pattern cannot open a phantom string.
        if ch == "/" and _opens_regex(text, i):
            j = _skip_regex_literal(text, i)
            if j is not None:
                line += text[i:j].count("\n")
                i = j
                continue
        # Triple-quoted strings (python docstrings, dart multi-line)
        for triple in ('"""', "'''"):
            if text.startswith(triple, i):
                j = text.find(triple, i + 3)
                j = n if j == -1 else j + 3
                add_string_span(line, i, j)
                line += text[i:j].count("\n")
                i = j
                break
        else:
            # Template literal: legally spans lines, so it gets its own scanner.
            if ch == "`":
                j, chunks = _scan_template_literal(text, i, line)
                for chunk_line, chunk_start, chunk_end in chunks:
                    add_string_span(chunk_line, chunk_start, chunk_end)
                line += text[i:j].count("\n")
                i = j
                continue
            # Single/double-quoted string literal on one logical line
            if ch in ("'", '"'):
                j = i + 1
                while j < n and text[j] != ch:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == "\n":
                        break  # unterminated on this line; stop to stay safe
                    j += 1
                end = j + 1
                add_string_span(line, i, end)
                line += text[i:end].count("\n")
                i = end
                continue
            if ch == "\n":
                line += 1
            i += 1
            continue
        continue  # the for/else `break` path (triple-quote) lands here
    return spans


def extract_embedded_text(text: str) -> list[tuple[int, str]]:
    """Return (line_number, natural-language-fragment) pairs from source code.

    Thin wrapper over `_extract_embedded_spans`: maps each span to its text. See
    that function for the extraction contract (comments and string literals in,
    identifiers and logic out).
    """
    return [(ln, text[start:end]) for (ln, start, end) in _extract_embedded_spans(text)]


def _correct_token(token: str) -> str | None:
    """Return the accented form of a single lowercase-or-mixed-case token, or None.

    Consults the explicit dictionary first, then the suffix heuristic. Preserves
    the original capitalization. Used by --fix.
    """
    low = token.lower()
    if low in PT_BR_CONTEXT_REQUIRED:
        # Correctness depends on the following word, which a single-token fixer
        # cannot see. Never rewrite it blindly; check_ptbr reports it in context.
        return None
    if low in PT_BR_FILLER_UNACCENTED and PT_BR_FILLER_UNACCENTED[low] != low:
        return _apply_case(PT_BR_FILLER_UNACCENTED[low], token)
    suggestion = _suffix_suggestion(low)
    if suggestion and suggestion != low:
        return _apply_case(suggestion, token)
    return None


def _fix_prose_fragment(fragment: str) -> str:
    """Apply diacritic corrections to every word in one prose fragment.

    Only word tokens are touched, and only when a high-confidence correction
    exists; punctuation, code-y characters, and untouched words pass through.
    Dash glyphs are NOT auto-fixed (the right replacement is context-dependent).
    """
    lower = fragment.lower()
    evidence: bool | None = None  # lazy: computed only when an ambiguous token shows up

    def repl(m: re.Match) -> str:
        nonlocal evidence
        token = m.group(0)
        if token.lower() in PT_VERB_AMBIGUOUS_TOKENS:
            return token  # verb vs noun: a human decides, never the fixer
        if token.lower() in PT_AMBIGUOUS_EN_TOKENS:
            if evidence is None:
                evidence = _has_ptbr_evidence(lower)
            if not evidence:
                return token
        fixed = _correct_token(token)
        return fixed if fixed is not None else token

    return re.sub(r"\b\w+\b", repl, fragment, flags=re.UNICODE)


def fix_source_text(text: str) -> tuple[str, int]:
    """Rewrite only the embedded prose (comments + string literals) of source.

    Re-extracts the embedded fragments with their character offsets and rebuilds
    the file, correcting diacritics inside each fragment and leaving every byte
    of code (identifiers, operators, structure) untouched. Returns the new text
    and the number of fragments changed.
    """
    spans = _extract_embedded_spans(text)
    if not spans:
        return text, 0
    out = []
    cursor = 0
    changed = 0
    for _line, start, end in spans:
        out.append(text[cursor:start])
        original = text[start:end]
        fixed = _fix_prose_fragment(original)
        if fixed != original:
            changed += 1
        out.append(fixed)
        cursor = end
    out.append(text[cursor:])
    return "".join(out), changed


def check_dashes(file: Path, lines: Iterable[tuple[int, str]], lang: str) -> list[Finding]:
    findings: list[Finding] = []
    for lineno, raw in lines:
        # Inline back-quoted spans are quoted commands and identifiers, not
        # prose, so their punctuation is not ours to restyle: ``npm test -- info``
        # uses ` -- ` as npm's argument separator and rewriting it breaks the
        # command. The markdown track already strips these spans upstream; doing
        # it here extends the same protection to embedded source fragments.
        text = re.sub(r"`[^`]*`", "", raw)
        if any(glyph in text for glyph in DASH_GLYPHS):
            findings.append(
                Finding(
                    file,
                    lineno,
                    "error",
                    "Literal em/en dash glyph is not allowed; restructure with comma, "
                    "parenthesis, colon, or period (PT-BR keeps it only for dialogue).",
                )
            )
        # Spaced double hyphen used as a dash. check_ptbr already flags it for
        # PT-BR; EN now bans it too.
        if lang == "en" and (" -- " in text or text.startswith("-- ") or text.endswith(" --")):
            findings.append(
                Finding(
                    file,
                    lineno,
                    "warning",
                    "EN: ' -- ' is not allowed as punctuation; restructure with comma, "
                    "parenthesis, colon, or period.",
                )
            )
    return findings


def _apply_case(template: str, sample: str) -> str:
    """Copy the capitalization of `sample` onto `template` (both lowercased)."""
    if sample.isupper():
        return template.upper()
    if sample[:1].isupper():
        return template[:1].upper() + template[1:]
    return template


def _suffix_suggestion(token_lower: str) -> str | None:
    """Return the accented form if `token_lower` hits the -ção/-são heuristic.

    Generic, allowlist-free: any word ending in -cao/-coes/-sao/-soes (length
    guard excludes the short native -ao words) almost always takes the accented
    ending. Returns None when no rule applies or the word is an exception.
    """
    if token_lower in PT_BR_SUFFIX_EXCEPTIONS:
        return None
    for bare_suffix, accented_suffix in PT_BR_SUFFIX_RULES:
        if token_lower.endswith(bare_suffix) and len(token_lower) >= len(bare_suffix) + 2:
            stem = token_lower[: -len(bare_suffix)]
            return stem + accented_suffix
    return None


def check_ptbr(file: Path, lines: Iterable[tuple[int, str]]) -> list[Finding]:
    findings: list[Finding] = []
    for lineno, text in lines:
        lower = text.lower()
        flagged_spans: set[tuple[int, int]] = set()
        evidence: bool | None = None  # lazy, per line
        # 1) Explicit dictionary: high-frequency words (covers accents the suffix
        #    heuristic cannot derive, e.g. proparoxytones like "código").
        for bare, accented in PT_BR_FILLER_UNACCENTED.items():
            if bare == accented:  # identity guards (EN terms / no-accent words)
                continue
            if bare in PT_AMBIGUOUS_EN_TOKENS:
                if evidence is None:
                    evidence = _has_ptbr_evidence(lower)
                if not evidence:
                    continue
            context = PT_BR_CONTEXT_REQUIRED.get(bare)
            for m in re.finditer(rf"\b{re.escape(bare)}\b", lower):
                if context and not context.match(lower, m.start()):
                    continue  # correct unaccented reading in this context
                flagged_spans.add((m.start(), m.end()))
                if bare in PT_VERB_AMBIGUOUS_TOKENS:
                    findings.append(
                        Finding(
                            file,
                            lineno,
                            "warning",
                            f"PT-BR: '{bare}' reads as the verb here or as the "
                            f"noun '{accented}'. Confirm which one the sentence "
                            f"needs; this is never auto-corrected.",
                        )
                    )
                    continue
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "error",
                        f"PT-BR: '{bare}' must be '{accented}' (mandatory diacritic)",
                        bare=bare,
                        suggestion=accented,
                    )
                )
        # 2) Generic suffix heuristic: every -ção/-são-family word not already in
        #    the dictionary. This is what removes the allowlist ceiling.
        for m in re.finditer(r"\b[a-z]{4,}\b", lower):
            if (m.start(), m.end()) in flagged_spans:
                continue
            token = m.group(0)
            suggestion = _suffix_suggestion(token)
            if suggestion and suggestion != token:
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "error",
                        f"PT-BR: '{token}' must be '{suggestion}' (mandatory diacritic)",
                        bare=token,
                        suggestion=suggestion,
                    )
                )
        # Back-quoted spans hold commands, not prose: ``npm test -- info`` uses
        # ` -- ` as npm's argument separator, so restyling it breaks the command.
        dash_probe = re.sub(r"`[^`]*`", "", text)
        if (
            " -- " in dash_probe
            or dash_probe.startswith("-- ")
            or dash_probe.endswith(" --")
        ):
            findings.append(
                Finding(
                    file,
                    lineno,
                    "warning",
                    "' -- ' is not allowed as punctuation; restructure with comma, "
                    "parenthesis, colon, or period (PT-BR reserves the em dash for dialogue).",
                )
            )
    return findings


def check_en(file: Path, lines: Iterable[tuple[int, str]]) -> list[Finding]:
    findings: list[Finding] = []
    for lineno, text in lines:
        for pattern in EN_FORBIDDEN_PHRASES:
            if re.search(pattern, text, re.IGNORECASE):
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "error",
                        f"EN: forbidden padding phrase matching /{pattern}/. Drop or rewrite.",
                    )
                )
        for pattern in EN_FILLER_WORDS:
            if re.search(pattern, text, re.IGNORECASE):
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "warning",
                        f"EN: filler word matching /{pattern}/. Consider dropping.",
                    )
                )
    return findings


def _language_words(text: str) -> list[str]:
    """Return lowercase Unicode word tokens from one Markdown prose line."""
    return re.findall(r"[^\W\d_]+", text.casefold(), flags=re.UNICODE)


def _normalized_label(text: str) -> str:
    """Normalize a heading or table label for exact high-confidence matching."""
    text = re.sub(r"<!--|-->", " ", text)
    text = re.sub(r"\b(?:RFC|ADR|ATA|QAS)-?[A-Z0-9{}-]*\b", " ", text, flags=re.IGNORECASE)
    text = re.sub(r"[^0-9A-Za-zÀ-ÖØ-öø-ÿ/ ]+", " ", text)
    return " ".join(text.casefold().split())


def check_markdown_language_consistency(
    file: Path,
    lines: Iterable[tuple[int, str]],
    lang: str,
) -> list[Finding]:
    """Flag high-confidence opposite-language narrative in Markdown.

    The semantic `artifact-writer` remains responsible for translation. This
    deterministic gate only catches strong signals: known documentation
    headings, metadata labels, and prose lines dominated by opposite-language
    function words. Technical English terms such as cache, rollback, API, and
    RFC are intentionally absent from the marker sets.
    """
    findings: list[Finding] = []
    for lineno, text in lines:
        stripped = text.strip()
        if not stripped or stripped == "---":
            continue

        heading_match = re.match(r"^#{1,6}\s+(.+?)\s*$", stripped)
        if heading_match:
            label = _normalized_label(heading_match.group(1))
            words = _language_words(label)
            en_count = sum(word in EN_HEADING_MARKERS for word in words)
            pt_count = sum(word in PT_HEADING_MARKERS for word in words)
            mismatch = (
                lang == "pt-BR"
                and (
                    label in EN_SINGLETON_HEADINGS
                    or (en_count >= 2 and en_count >= max(2, pt_count * 2))
                )
            ) or (
                lang == "en"
                and (
                    label in PT_SINGLETON_HEADINGS
                    or (pt_count >= 2 and pt_count >= max(2, en_count * 2))
                )
            )
            if mismatch:
                target = "Brazilian Portuguese" if lang == "pt-BR" else "English"
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "error",
                        f"{lang}: heading/title appears to use the wrong narrative "
                        f"language; localize it to {target}.",
                    )
                )
            continue

        if stripped.startswith("|") and not re.match(r"^\|?[\s:|-]+\|?$", stripped):
            cells = [
                _normalized_label(cell)
                for cell in stripped.strip("|").split("|")
                if _normalized_label(cell)
            ]
            wrong_labels = EN_TABLE_LABELS if lang == "pt-BR" else PT_TABLE_LABELS
            if any(cell in wrong_labels for cell in cells):
                target = "PT-BR" if lang == "pt-BR" else "EN"
                findings.append(
                    Finding(
                        file,
                        lineno,
                        "error",
                        f"{lang}: metadata or table label uses the wrong narrative "
                        f"language; localize the label to {target}.",
                    )
                )
            continue

        # Frontmatter/config fields and pure Markdown structure are protected
        # tokens, not narrative prose.
        if re.match(r"^[A-Za-z0-9_-]+:\s*", stripped):
            continue

        # A quoted span is a sample, not the author's narrative: a routing table
        # that lists the Portuguese utterances an English document must
        # recognize is still an English document.
        classified = re.sub(r"\"[^\"]*\"", "", stripped)
        words = _language_words(classified)
        # Shared short tokens such as "a" and "as" carry no language signal.
        # Count only stopwords unique to one language.
        en_count = sum(
            word in EN_STOPWORDS and word not in PT_STOPWORDS for word in words
        )
        pt_count = sum(
            word in PT_STOPWORDS and word not in EN_STOPWORDS for word in words
        )
        mismatch = (
            lang == "pt-BR" and en_count >= 4 and en_count >= max(4, pt_count * 3)
        ) or (
            lang == "en" and pt_count >= 4 and pt_count >= max(4, en_count * 3)
        )
        if mismatch:
            target = "Brazilian Portuguese" if lang == "pt-BR" else "English"
            findings.append(
                Finding(
                    file,
                    lineno,
                    "error",
                    f"{lang}: narrative prose appears to use the wrong language; "
                    f"localize the complete sentence to {target}.",
                )
            )
    return findings


# Roots whose Markdown is a project artifact governed by the project's
# configured language. Everything else (framework source, adapters, installed
# skills) stays EN by authoring convention, so the index must not reach it.
_PROJECT_ARTIFACT_ROOTS = {"docs", ".araia"}


@lru_cache(maxsize=None)
def _index_language(root: Path) -> str | None:
    """Read the configured language from `root/.araia/index.md`."""
    index = root / ".araia" / "index.md"
    try:
        text = index.read_text(encoding="utf-8")
    except OSError:
        return None
    fm = re.match(r"^---\n(.*?)\n---", text, re.DOTALL)
    if not fm:
        return None
    match = re.search(r"^language:\s*['\"]?([A-Za-z-]+)", fm.group(1), re.MULTILINE)
    if not match:
        return None
    return "pt-BR" if match.group(1).lower() == "pt-br" else "en"


def _project_language(path: Path) -> str | None:
    """Resolve the configured language for a project artifact.

    `shared/language-detection.md` orders resolution as `--lang`, then the
    artifact's own `language:`, then the project index. A file with no
    frontmatter carries no language of its own, so the index is the next
    authority. Defaulting straight to EN misreported every frontmatter-free
    PT-BR artifact, including run state under `.araia/runs/`.

    The index governs only project artifacts. Framework source is authored in
    EN regardless of the project's language, so consulting the index for it
    would flag correct EN prose as the wrong language.
    """
    for directory in path.parents:
        if not (directory / ".araia" / "index.md").is_file():
            continue
        try:
            relative = path.relative_to(directory)
        except ValueError:
            return None
        if relative.parts and relative.parts[0] in _PROJECT_ARTIFACT_ROOTS:
            return _index_language(directory)
        return None
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Lint markdown artifacts for PT-BR / EN writing rules.")
    parser.add_argument("files", nargs="+", type=Path)
    parser.add_argument(
        "--lang",
        choices=["pt-BR", "en", "auto"],
        default="auto",
        help="Override language detection. 'auto' reads frontmatter `language:` or defaults to EN.",
    )
    parser.add_argument(
        "--mode",
        choices=["markdown", "source", "auto"],
        default="auto",
        help="Input kind. 'auto' picks by extension: markdown for .md/.txt/..., "
        "source for .dart/.ts/.py/... In source mode only comments and string "
        "literals are checked (identifiers are never read).",
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit non-zero on any finding (default: warn-only, exit 0 unless --strict).",
    )
    parser.add_argument(
        "--github",
        action="store_true",
        help="Emit GitHub Actions annotations.",
    )
    parser.add_argument(
        "--fix",
        action="store_true",
        help="Apply the high-confidence diacritic corrections in place (only "
        "inside comments and string literals for source files; dash glyphs and "
        "EN-phrase findings are never auto-fixed). Re-runs the check after "
        "writing; remaining findings still set the exit code.",
    )
    args = parser.parse_args()

    # The linter and its test are the one place that legitimately contains the
    # unaccented forms and dash glyphs as DATA (the rule dictionary, the suffix
    # table, the DASH_GLYPHS tuple). Scanning them would flag the rules
    # themselves, so the detector exempts itself, mirroring how no-spec-refs-scan
    # exempts the spec roots. Any other file gets the full treatment.
    SELF_EXEMPT = {"check-writing-rules.py", "test_check_writing_rules.py"}

    findings: list[Finding] = []
    for file in args.files:
        if not file.exists():
            print(f"FAIL: {file} not found", file=sys.stderr)
            return 2
        if file.name in SELF_EXEMPT:
            continue
        # utf-8-sig: strips a BOM when present (PowerShell-written files carry
        # one), identical to utf-8 otherwise. A BOM before `---` would defeat
        # the frontmatter match and misroute language detection.
        text = file.read_text(encoding="utf-8-sig")

        mode = args.mode
        if mode == "auto":
            e = file.suffix.lstrip(".").lower()
            if e in SOURCE_EXTENSIONS:
                mode = "source"
            elif e in MARKDOWN_EXTENSIONS:
                mode = "markdown"
            else:
                # Unknown extension: treat as markdown (whole-line), the
                # canonical conservative behavior.
                mode = "markdown"

        # Conservative auto-fix (source only): rewrite diacritics inside comments
        # and string literals, never code. Dash glyphs are left for a human (the
        # correct replacement is context-dependent). The post-fix text is what
        # the check below then reports on, so --fix and the exit code compose.
        if args.fix and mode == "source":
            fixed_text, changed = fix_source_text(text)
            if changed:
                file.write_text(fixed_text, encoding="utf-8")
                text = fixed_text
                print(f"FIXED: {file} ({changed} fragment(s) corrected)", file=sys.stderr)

        lang = args.lang
        if lang == "auto":
            if mode == "source":
                # Source files have no frontmatter. The PT-BR diacritic check is
                # itself language-selective (it only matches PT-BR words), so we
                # always run it on the extracted prose; dashes are language-
                # agnostic. EN-filler checks would over-fire on code, so skip.
                lang = "pt-BR"
            else:
                fm = re.match(r"^---\n(.*?)\n---", text, re.DOTALL)
                # Anchored to line start: a `language:` FIELD, not the substring
                # "language: pt-BR" inside a description value ("Default
                # language: pt-BR."), which misclassified EN skills as PT-BR.
                if fm and re.search(r"^language:\s*['\"]?pt-BR", fm.group(1), re.IGNORECASE | re.MULTILINE):
                    lang = "pt-BR"
                elif fm and re.search(r"^language:\s*['\"]?en", fm.group(1), re.IGNORECASE | re.MULTILINE):
                    lang = "en"
                else:
                    # No `language:` of its own: fall back to the project index
                    # before EN, per the documented resolution order.
                    lang = _project_language(file.resolve().parent) or "en"

        if mode == "source":
            cleaned = extract_embedded_text(text)
            # In source mode only the high-confidence, language-agnostic checks
            # run: PT-BR diacritics and forbidden dash glyphs. EN padding/filler
            # phrase checks stay markdown-only (they over-fire on code prose).
            findings.extend(check_dashes(file, cleaned, "pt-BR"))
            findings.extend(check_ptbr(file, cleaned))
        else:
            cleaned = strip_code(text)
            findings.extend(check_dashes(file, cleaned, lang))
            findings.extend(check_markdown_language_consistency(file, cleaned, lang))
            if lang == "pt-BR":
                findings.extend(check_ptbr(file, cleaned))
            else:
                findings.extend(check_en(file, cleaned))

    if not findings:
        print(f"PASS: {len(args.files)} file(s) checked")
        return 0

    errors = [f for f in findings if f.severity == "error"]
    warnings = [f for f in findings if f.severity == "warning"]

    for finding in findings:
        print(finding.format_github() if args.github else str(finding))

    print(f"\nSummary: {len(errors)} error(s), {len(warnings)} warning(s)", file=sys.stderr)

    if args.strict and errors:
        return 2
    if args.strict:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
