// agwinterm-lite control-API server (M1 phase 2): the same newline-JSON protocol
// the main app serves, so agwintermctl, the Claude skill, and hooks work against
// lite UNCHANGED (the control API deliberately stays JSON — it is the human/
// scripting surface; the pty-host protocol is the protobuf one).
//
// Serves a subset: ping, tree, session.new, session.select, session.close,
// session.type, session.text, session.status. Thread-per-client, sync pipes,
// strict request/response.
#pragma once

#include <windows.h>
#include <string>
#include <map>

// ---- tiny JSON: parse one request object into the fields the API subset uses.
// Full escapes on strings; nested "args" is flattened as "args.<key>". ----
struct JsonReq {
    std::map<std::string, std::string> fields;
    const std::string& get(const std::string& k) const {
        static const std::string empty;
        auto it = fields.find(k);
        return it == fields.end() ? empty : it->second;
    }
};

inline bool jsonParseString(const std::string& s, size_t& i, std::string& out) {
    if (s[i] != '"') return false;
    i++;
    out.clear();
    while (i < s.size() && s[i] != '"') {
        char c = s[i++];
        if (c == '\\' && i < s.size()) {
            char e = s[i++];
            switch (e) {
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'u': {
                    if (i + 4 > s.size()) return false;
                    unsigned code = strtoul(s.substr(i, 4).c_str(), nullptr, 16);
                    i += 4;
                    // UTF-8 encode (BMP; surrogate pairs re-combine)
                    if (code >= 0xD800 && code <= 0xDBFF && i + 6 <= s.size() && s[i] == '\\' && s[i + 1] == 'u') {
                        unsigned lo = strtoul(s.substr(i + 2, 4).c_str(), nullptr, 16);
                        i += 6;
                        unsigned cp = 0x10000 + ((code - 0xD800) << 10) + (lo - 0xDC00);
                        out += (char)(0xF0 | (cp >> 18));
                        out += (char)(0x80 | ((cp >> 12) & 0x3F));
                        out += (char)(0x80 | ((cp >> 6) & 0x3F));
                        out += (char)(0x80 | (cp & 0x3F));
                    } else if (code < 0x80) out += (char)code;
                    else if (code < 0x800) {
                        out += (char)(0xC0 | (code >> 6));
                        out += (char)(0x80 | (code & 0x3F));
                    } else {
                        out += (char)(0xE0 | (code >> 12));
                        out += (char)(0x80 | ((code >> 6) & 0x3F));
                        out += (char)(0x80 | (code & 0x3F));
                    }
                    break;
                }
                default: out += e; break;
            }
        } else out += c;
    }
    if (i >= s.size()) return false;
    i++; // closing quote
    return true;
}

inline void jsonSkipWs(const std::string& s, size_t& i) {
    while (i < s.size() && (s[i] == ' ' || s[i] == '\t')) i++;
}

// Parses {"k":"v"|number|true|false|{...}} one level deep (args flattened).
inline bool jsonParseObject(const std::string& s, size_t& i, const std::string& prefix, JsonReq& out) {
    jsonSkipWs(s, i);
    if (i >= s.size() || s[i] != '{') return false;
    i++;
    for (;;) {
        jsonSkipWs(s, i);
        if (i < s.size() && s[i] == '}') { i++; return true; }
        std::string key;
        if (!jsonParseString(s, i, key)) return false;
        jsonSkipWs(s, i);
        if (i >= s.size() || s[i] != ':') return false;
        i++;
        jsonSkipWs(s, i);
        if (i >= s.size()) return false;
        if (s[i] == '"') {
            std::string val;
            if (!jsonParseString(s, i, val)) return false;
            out.fields[prefix + key] = val;
        } else if (s[i] == '{') {
            if (!jsonParseObject(s, i, prefix + key + ".", out)) return false;
        } else if (s[i] == '[') {   // arrays: skip balanced (unused by the subset)
            int depth = 0;
            do {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') depth--;
                else if (s[i] == '"') { std::string sk; if (!jsonParseString(s, i, sk)) return false; continue; }
                i++;
            } while (i < s.size() && depth > 0);
        } else {   // number / true / false / null
            size_t start = i;
            while (i < s.size() && s[i] != ',' && s[i] != '}') i++;
            std::string raw = s.substr(start, i - start);
            while (!raw.empty() && (raw.back() == ' ' || raw.back() == '\t')) raw.pop_back();
            out.fields[prefix + key] = raw;
        }
        jsonSkipWs(s, i);
        if (i < s.size() && s[i] == ',') { i++; continue; }
    }
}

inline std::string jsonEscape(const std::string& s) {
    std::string out;
    for (char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if ((unsigned char)c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); out += b; }
                else out += c;
        }
    }
    return out;
}

inline std::string ctlOk(const std::string& resultJson) { return "{\"ok\":true,\"result\":" + resultJson + "}"; }
inline std::string ctlOkStr(const std::string& s) { return ctlOk("\"" + jsonEscape(s) + "\""); }
inline std::string ctlErr(const std::string& msg) { return "{\"ok\":false,\"error\":\"" + jsonEscape(msg) + "\"}"; }
