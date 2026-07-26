#include "transcript_text_normalizer.hpp"

#include <algorithm>
#include <cstdint>
#include <string_view>
#include <utility>
#include <vector>

namespace meetingai::transcribe {
namespace {

struct LanguageCounts {
    int latinLetters = 0;
    int latinWords = 0;
    int cjkCharacters = 0;
};

bool IsAsciiLetter(char32_t ch)
{
    return (ch >= U'A' && ch <= U'Z') || (ch >= U'a' && ch <= U'z');
}

bool IsAsciiUpper(char32_t ch)
{
    return ch >= U'A' && ch <= U'Z';
}

bool IsAsciiLower(char32_t ch)
{
    return ch >= U'a' && ch <= U'z';
}

bool IsCjkCharacter(char32_t ch)
{
    return
        (ch >= 0x3400 && ch <= 0x4DBF) ||  // CJK Extension A
        (ch >= 0x4E00 && ch <= 0x9FFF) ||  // CJK Unified Ideographs
        (ch >= 0xF900 && ch <= 0xFAFF) ||  // CJK Compatibility Ideographs
        (ch >= 0x3040 && ch <= 0x30FF) ||  // Hiragana and Katakana
        (ch >= 0xAC00 && ch <= 0xD7AF);    // Hangul syllables
}

bool IsSentenceTerminator(char32_t ch)
{
    return ch == U'.' || ch == U'?' || ch == U'!' ||
        ch == 0x3002 || ch == 0xFF0E || ch == 0xFF1F || ch == 0xFF01;
}

bool IsWhitespace(char32_t ch);

bool IsSentenceBoundaryAt(
    const std::vector<char32_t>& text,
    size_t index)
{
    if (index >= text.size() || !IsSentenceTerminator(text[index])) {
        return false;
    }
    if (text[index] != U'.' && text[index] != 0xFF0E) {
        return true;
    }

    const char32_t previous =
        index == 0 ? U'\0' : text[index - 1];
    size_t nextIndex = index + 1;
    while (nextIndex < text.size() && IsWhitespace(text[nextIndex])) {
        ++nextIndex;
    }
    const char32_t next =
        nextIndex < text.size() ? text[nextIndex] : U'\0';

    // 小数点不是句界，例如 3.14。
    if (previous >= U'0' && previous <= U'9' &&
        next >= U'0' && next <= U'9') {
        return false;
    }

    // 单字母缩写中的点不是句界，例如 U.S.、A.I.。
    if (IsAsciiLetter(previous)) {
        if (index + 1 < text.size() &&
            IsAsciiLetter(text[index + 1])) {
            size_t tokenLength = 0;
            size_t cursor = index;
            while (cursor > 0 && IsAsciiLetter(text[cursor - 1])) {
                --cursor;
                ++tokenLength;
            }
            if (tokenLength == 1) {
                return false;
            }
        }
        if (next != U'\0' &&
            index >= 3 &&
            text[index - 2] == U'.' &&
            IsAsciiLetter(text[index - 3])) {
            return false;
        }
    }

    return true;
}

bool IsWordConnector(char32_t ch)
{
    return ch == U'\'' || ch == 0x2019 || ch == U'-';
}

bool IsWhitespace(char32_t ch)
{
    return ch == U' ' || ch == U'\t' || ch == U'\r' || ch == U'\n' ||
        ch == 0x3000;
}

bool IsPunctuation(char32_t ch)
{
    switch (ch) {
    case U'.':
    case U',':
    case U'?':
    case U'!':
    case U':':
    case U';':
    case U'(':
    case U')':
    case 0x3001:
    case 0x3002:
    case 0xFF01:
    case 0xFF08:
    case 0xFF09:
    case 0xFF0C:
    case 0xFF0E:
    case 0xFF1A:
    case 0xFF1B:
    case 0xFF1F:
        return true;
    default:
        return false;
    }
}

bool IsAsciiSentencePunctuation(char32_t ch)
{
    return ch == U'.' || ch == U',' || ch == U'?' || ch == U'!' ||
        ch == U':' || ch == U';';
}

bool DecodeUtf8(std::string_view input, std::vector<char32_t>& output)
{
    output.clear();
    output.reserve(input.size());

    for (size_t i = 0; i < input.size();) {
        const auto lead = static_cast<unsigned char>(input[i]);
        char32_t codePoint = 0;
        size_t length = 0;

        if (lead < 0x80) {
            codePoint = lead;
            length = 1;
        }
        else if ((lead & 0xE0) == 0xC0) {
            codePoint = lead & 0x1F;
            length = 2;
        }
        else if ((lead & 0xF0) == 0xE0) {
            codePoint = lead & 0x0F;
            length = 3;
        }
        else if ((lead & 0xF8) == 0xF0) {
            codePoint = lead & 0x07;
            length = 4;
        }
        else {
            return false;
        }

        if (i + length > input.size()) {
            return false;
        }

        for (size_t offset = 1; offset < length; ++offset) {
            const auto next = static_cast<unsigned char>(input[i + offset]);
            if ((next & 0xC0) != 0x80) {
                return false;
            }
            codePoint = (codePoint << 6) | (next & 0x3F);
        }

        const bool overlong =
            (length == 2 && codePoint < 0x80) ||
            (length == 3 && codePoint < 0x800) ||
            (length == 4 && codePoint < 0x10000);
        if (overlong || codePoint > 0x10FFFF ||
            (codePoint >= 0xD800 && codePoint <= 0xDFFF)) {
            return false;
        }

        output.push_back(codePoint);
        i += length;
    }

    return true;
}

std::string EncodeUtf8(const std::vector<char32_t>& input)
{
    std::string output;
    output.reserve(input.size() * 2);

    for (char32_t ch : input) {
        if (ch <= 0x7F) {
            output.push_back(static_cast<char>(ch));
        }
        else if (ch <= 0x7FF) {
            output.push_back(static_cast<char>(0xC0 | (ch >> 6)));
            output.push_back(static_cast<char>(0x80 | (ch & 0x3F)));
        }
        else if (ch <= 0xFFFF) {
            output.push_back(static_cast<char>(0xE0 | (ch >> 12)));
            output.push_back(static_cast<char>(0x80 | ((ch >> 6) & 0x3F)));
            output.push_back(static_cast<char>(0x80 | (ch & 0x3F)));
        }
        else {
            output.push_back(static_cast<char>(0xF0 | (ch >> 18)));
            output.push_back(static_cast<char>(0x80 | ((ch >> 12) & 0x3F)));
            output.push_back(static_cast<char>(0x80 | ((ch >> 6) & 0x3F)));
            output.push_back(static_cast<char>(0x80 | (ch & 0x3F)));
        }
    }

    return output;
}

LanguageCounts CountLanguages(
    const std::vector<char32_t>& text,
    size_t begin,
    size_t end)
{
    LanguageCounts counts;
    bool inLatinWord = false;

    for (size_t i = begin; i < end; ++i) {
        const char32_t ch = text[i];
        if (IsAsciiLetter(ch)) {
            ++counts.latinLetters;
            if (!inLatinWord) {
                ++counts.latinWords;
                inLatinWord = true;
            }
        }
        else {
            if (!IsWordConnector(ch)) {
                inLatinWord = false;
            }
            if (IsCjkCharacter(ch)) {
                ++counts.cjkCharacters;
            }
        }
    }

    return counts;
}

bool IsEnglishDominant(const LanguageCounts& counts)
{
    if (counts.latinLetters == 0) {
        return false;
    }
    if (counts.cjkCharacters == 0) {
        return true;
    }

    // Mixed-language sentences default to Chinese punctuation unless the
    // surrounding sentence is overwhelmingly English. This prevents a lone
    // product name such as "OpenVINO" from changing a Chinese sentence.
    return counts.latinWords >= 3 &&
        counts.latinLetters >= counts.cjkCharacters * 4;
}

std::pair<size_t, size_t> FindSentenceBounds(
    const std::vector<char32_t>& text,
    size_t position)
{
    size_t begin = position;
    while (begin > 0 && !IsSentenceBoundaryAt(text, begin - 1)) {
        --begin;
    }

    size_t end = position;
    if (position < text.size() && !IsSentenceBoundaryAt(text, position)) {
        while (end < text.size() && !IsSentenceBoundaryAt(text, end)) {
            ++end;
        }
    }

    return { begin, end };
}

char32_t ToEnglishPunctuation(char32_t ch)
{
    switch (ch) {
    case 0x3002: // ideographic full stop
    case 0xFF0E: return U'.';
    case 0xFF0C: return U',';
    case 0xFF1F: return U'?';
    case 0xFF01: return U'!';
    case 0xFF1A: return U':';
    case 0xFF1B: return U';';
    case 0x3001: return U',';
    case 0xFF08: return U'(';
    case 0xFF09: return U')';
    default: return ch;
    }
}

std::string AsciiTokenUpper(
    const std::vector<char32_t>& text,
    size_t begin,
    size_t end)
{
    std::string token;
    token.reserve(end - begin);
    for (size_t i = begin; i < end; ++i) {
        const char32_t ch = text[i];
        if (ch <= 0x7F) {
            token.push_back(static_cast<char>(
                IsAsciiLower(ch) ? ch - U'a' + U'A' : ch));
        }
    }
    return token;
}

bool IsPreservedAcronym(const std::string& token)
{
    static constexpr std::string_view acronyms[] = {
        "AI", "API", "ASR", "CPU", "GPU", "NPU", "NLP", "LLM", "RAG",
        "ONNX", "VAD", "SDK", "UI", "UX", "SQL", "JSON", "XML", "HTTP",
        "HTTPS", "UTF", "PDF", "RAM", "ROM", "USB", "DNS", "TCP", "IP",
        "PC", "TV", "USA", "UK", "EU", "IBM", "AMD"
    };

    return std::find(std::begin(acronyms), std::end(acronyms), token) !=
        std::end(acronyms);
}

std::string_view CanonicalBrand(const std::string& token)
{
    static constexpr std::pair<std::string_view, std::string_view> brands[] = {
        { "OPENAI", "OpenAI" },
        { "CHATGPT", "ChatGPT" },
        { "OPENVINO", "OpenVINO" },
        { "YOUTUBE", "YouTube" },
        { "GITHUB", "GitHub" },
        { "MICROSOFT", "Microsoft" },
        { "WINDOWS", "Windows" },
        { "WHISPER", "Whisper" }
    };

    for (const auto& [upper, canonical] : brands) {
        if (upper == token) {
            return canonical;
        }
    }
    return {};
}

void ReplaceAsciiToken(
    std::vector<char32_t>& text,
    size_t begin,
    size_t end,
    std::string_view replacement)
{
    if (replacement.size() != end - begin) {
        return;
    }
    for (size_t i = 0; i < replacement.size(); ++i) {
        text[begin + i] = static_cast<unsigned char>(replacement[i]);
    }
}

void NormalizeEnglishCasing(
    std::vector<char32_t>& text,
    size_t begin,
    size_t end)
{
    const LanguageCounts language = CountLanguages(text, begin, end);
    if (language.latinLetters == 0) {
        return;
    }

    int uppercase = 0;
    int lowercase = 0;
    for (size_t i = begin; i < end; ++i) {
        uppercase += IsAsciiUpper(text[i]) ? 1 : 0;
        lowercase += IsAsciiLower(text[i]) ? 1 : 0;
    }

    // Do not touch naturally mixed-case text. Sherpa's all-caps output is
    // recognizable by a high uppercase ratio.
    const int casedLetters = uppercase + lowercase;
    if (casedLetters < 3 || uppercase * 100 < casedLetters * 85) {
        return;
    }

    // In a Chinese-dominant sentence, an embedded English phrase normally
    // starts lowercase. A pure/predominantly English sentence starts uppercase.
    bool firstWord = IsEnglishDominant(language);
    for (size_t i = begin; i < end;) {
        if (!IsAsciiLetter(text[i])) {
            ++i;
            continue;
        }

        const size_t tokenBegin = i;
        while (i < end &&
            (IsAsciiLetter(text[i]) || IsWordConnector(text[i]))) {
            ++i;
        }
        const size_t tokenEnd = i;
        const std::string upperToken = AsciiTokenUpper(text, tokenBegin, tokenEnd);
        const std::string_view canonical = CanonicalBrand(upperToken);
        const bool dottedInitial =
            tokenEnd == tokenBegin + 1 &&
            ((tokenBegin > begin && text[tokenBegin - 1] == U'.') ||
             (tokenEnd < end && text[tokenEnd] == U'.'));

        if (!canonical.empty()) {
            ReplaceAsciiToken(text, tokenBegin, tokenEnd, canonical);
        }
        else if (!IsPreservedAcronym(upperToken) && !dottedInitial) {
            for (size_t j = tokenBegin; j < tokenEnd; ++j) {
                if (IsAsciiUpper(text[j])) {
                    text[j] = text[j] - U'A' + U'a';
                }
            }

            const bool isFirstPerson =
                text[tokenBegin] == U'i' &&
                (tokenEnd == tokenBegin + 1 ||
                    (tokenBegin + 1 < tokenEnd &&
                        (text[tokenBegin + 1] == U'\'' ||
                            text[tokenBegin + 1] == 0x2019)));
            if (firstWord || isFirstPerson) {
                text[tokenBegin] = text[tokenBegin] - U'a' + U'A';
            }
        }

        firstWord = false;
    }
}

bool NeedsEnglishSpaceAfter(
    const std::vector<char32_t>& text,
    size_t punctuationIndex,
    size_t nextIndex,
    bool hadWhitespace)
{
    if (punctuationIndex >= text.size() || nextIndex >= text.size()) {
        return false;
    }

    const char32_t punctuation = text[punctuationIndex];
    const char32_t next = text[nextIndex];
    const char32_t previous =
        punctuationIndex == 0 ? U'\0' : text[punctuationIndex - 1];

    if (!(IsAsciiLetter(next) ||
          (next >= U'0' && next <= U'9') ||
          next == U'(' || next == U'"' || next == U'\'')) {
        return false;
    }

    if (hadWhitespace) {
        return true;
    }

    // 小数和千位分隔符不能被拆开，例如 3.14、1,000。
    if ((punctuation == U'.' || punctuation == U',') &&
        previous >= U'0' && previous <= U'9' &&
        next >= U'0' && next <= U'9') {
        return false;
    }

    // 保留常见单字母缩写内部的点，例如 U.S.、A.I.。
    if (punctuation == U'.' &&
        IsAsciiLetter(previous) &&
        IsAsciiLetter(next)) {
        size_t tokenLength = 0;
        size_t cursor = punctuationIndex;
        while (cursor > 0 && IsAsciiLetter(text[cursor - 1])) {
            --cursor;
            ++tokenLength;
        }
        if (tokenLength == 1) {
            return false;
        }
    }

    return true;
}

void NormalizeEnglishPunctuationSpacing(std::vector<char32_t>& text)
{
    std::vector<char32_t> normalized;
    normalized.reserve(text.size() + text.size() / 12);

    for (size_t i = 0; i < text.size(); ++i) {
        const char32_t ch = text[i];
        if (!IsAsciiSentencePunctuation(ch)) {
            normalized.push_back(ch);
            continue;
        }

        const auto [begin, end] = FindSentenceBounds(text, i);
        if (!IsEnglishDominant(CountLanguages(text, begin, end))) {
            normalized.push_back(ch);
            continue;
        }

        while (!normalized.empty() && IsWhitespace(normalized.back())) {
            normalized.pop_back();
        }
        normalized.push_back(ch);

        size_t next = i + 1;
        while (next < text.size() && IsWhitespace(text[next])) {
            ++next;
        }
        if (NeedsEnglishSpaceAfter(text, i, next, next > i + 1)) {
            normalized.push_back(U' ');
        }
        i = next == 0 ? i : next - 1;
    }

    text = std::move(normalized);
}

} // namespace

std::string NormalizeBilingualTranscript(const std::string& text)
{
    if (text.empty()) {
        return text;
    }

    std::vector<char32_t> characters;
    if (!DecodeUtf8(text, characters)) {
        return text;
    }

    // First normalize only punctuation whose sentence context is English.
    // Chinese-dominant and mixed Chinese sentences remain untouched.
    for (size_t i = 0; i < characters.size(); ++i) {
        const char32_t converted = ToEnglishPunctuation(characters[i]);
        if (converted == characters[i]) {
            continue;
        }

        const auto [begin, end] = FindSentenceBounds(characters, i);
        if (IsEnglishDominant(CountLanguages(characters, begin, end))) {
            characters[i] = converted;
        }
    }

    // Then sentence-case only all-caps English sentences.
    size_t sentenceBegin = 0;
    for (size_t i = 0; i <= characters.size(); ++i) {
        if (i == characters.size() ||
            IsSentenceBoundaryAt(characters, i)) {
            NormalizeEnglishCasing(characters, sentenceBegin, i);
            sentenceBegin = i + 1;
        }
    }

    // sherpa 标点模型会把英文句号/逗号直接粘到下一个词上。
    // 在大小写和中英文标点已经确定后，再补齐英文排版空格。
    NormalizeEnglishPunctuationSpacing(characters);

    return EncodeUtf8(characters);
}

std::string JoinTranscriptFragments(
    const std::string& left,
    const std::string& right)
{
    if (left.empty()) {
        return right;
    }
    if (right.empty()) {
        return left;
    }

    std::vector<char32_t> leftCharacters;
    std::vector<char32_t> rightCharacters;
    if (!DecodeUtf8(left, leftCharacters) ||
        !DecodeUtf8(right, rightCharacters) ||
        leftCharacters.empty() ||
        rightCharacters.empty()) {
        return left + " " + right;
    }

    const char32_t last = leftCharacters.back();
    const char32_t first = rightCharacters.front();
    const bool needsSpace =
        !IsWhitespace(last) &&
        !IsWhitespace(first) &&
        !IsCjkCharacter(last) &&
        !IsCjkCharacter(first) &&
        !IsPunctuation(first);

    return needsSpace ? left + " " + right : left + right;
}

bool TryExtractStableTranscriptPrefix(
    const std::string& rawText,
    const std::string& punctuatedText,
    StableTranscriptPrefix& result)
{
    result = {};

    std::vector<char32_t> raw;
    std::vector<char32_t> punctuated;
    if (!DecodeUtf8(rawText, raw) ||
        !DecodeUtf8(punctuatedText, punctuated) ||
        raw.empty() ||
        punctuated.empty()) {
        return false;
    }

    struct Candidate {
        size_t punctuatedEnd = 0;
        size_t rawBoundary = 0;
    };
    std::vector<Candidate> candidates;

    size_t rawIndex = 0;
    for (size_t punctuatedIndex = 0;
         punctuatedIndex < punctuated.size();) {
        const char32_t output = punctuated[punctuatedIndex];

        if (IsWhitespace(output)) {
            while (punctuatedIndex < punctuated.size() &&
                IsWhitespace(punctuated[punctuatedIndex])) {
                ++punctuatedIndex;
            }
            while (rawIndex < raw.size() && IsWhitespace(raw[rawIndex])) {
                ++rawIndex;
            }
            continue;
        }

        while (rawIndex < raw.size() && IsWhitespace(raw[rawIndex])) {
            ++rawIndex;
        }

        const bool sameCharacter =
            rawIndex < raw.size() &&
            (output == raw[rawIndex] ||
                (IsAsciiLetter(output) && IsAsciiLetter(raw[rawIndex]) &&
                    (output | 0x20) == (raw[rawIndex] | 0x20)));

        if (sameCharacter) {
            ++rawIndex;
            ++punctuatedIndex;
            continue;
        }

        // The punctuation model only inserts punctuation; record the raw
        // boundary represented by an inserted sentence terminator.
        if (IsPunctuation(output)) {
            if (IsSentenceBoundaryAt(punctuated, punctuatedIndex)) {
                candidates.push_back({ punctuatedIndex + 1, rawIndex });
            }
            ++punctuatedIndex;
            continue;
        }

        // Unexpected rewriting means alignment is unsafe. Keep buffering and
        // fall back to the long-silence flush rather than dropping text.
        return false;
    }

    Candidate stable;
    bool found = false;
    for (const Candidate& candidate : candidates) {
        const LanguageCounts trailing =
            CountLanguages(raw, candidate.rawBoundary, raw.size());
        const int trailingUnits = trailing.latinWords + trailing.cjkCharacters;
        if (trailingUnits >= 3) {
            stable = candidate;
            found = true;
        }
    }
    if (!found) {
        return false;
    }

    std::vector<char32_t> finalized(
        punctuated.begin(),
        punctuated.begin() + static_cast<std::ptrdiff_t>(stable.punctuatedEnd));
    std::vector<char32_t> finalizedRaw(
        raw.begin(),
        raw.begin() + static_cast<std::ptrdiff_t>(stable.rawBoundary));
    std::vector<char32_t> remaining(
        raw.begin() + static_cast<std::ptrdiff_t>(stable.rawBoundary),
        raw.end());

    while (!finalized.empty() && IsWhitespace(finalized.back())) {
        finalized.pop_back();
    }
    while (!finalizedRaw.empty() && IsWhitespace(finalizedRaw.back())) {
        finalizedRaw.pop_back();
    }
    while (!remaining.empty() && IsWhitespace(remaining.front())) {
        remaining.erase(remaining.begin());
    }

    result.finalizedRawText = EncodeUtf8(finalizedRaw);
    result.finalizedText = EncodeUtf8(finalized);
    result.remainingRawText = EncodeUtf8(remaining);
    return !result.finalizedText.empty();
}

} // namespace meetingai::transcribe
