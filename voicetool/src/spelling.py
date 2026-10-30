"""American English spelling for everything VoiceTool types (the person speaking is American, so the
spelling is always like that). The speech model itself sometimes writes British spellings ("favour"), so this
runs as a fixed word list after speech-to-text and after every AI step. Whole words only, keeping capitals.
"""
import re

# British -> American. Explicit words only (no blanket "-ise" rule: advise, promise, surprise, exercise stay as they are).
_STEMS = {  # each stem also covers -s, -ed, -ing, -er, -ers, -able, -ite, -ites, -ful, -less where the word has them
    "favour": "favor", "colour": "color", "behaviour": "behavior", "honour": "honor", "labour": "labor",
    "neighbour": "neighbor", "rumour": "rumor", "humour": "humor", "flavour": "flavor", "harbour": "harbor",
    "savour": "savor", "endeavour": "endeavor", "armour": "armor", "vapour": "vapor", "parlour": "parlor",
    "realis": "realiz", "organis": "organiz", "recognis": "recogniz", "apologis": "apologiz", "prioritis": "prioritiz",
    "summaris": "summariz", "customis": "customiz", "optimis": "optimiz", "finalis": "finaliz",
    "criticis": "criticiz", "utilis": "utiliz", "minimis": "minimiz", "maximis": "maximiz", "categoris": "categoriz",
    "authoris": "authoriz", "standardis": "standardiz", "visualis": "visualiz", "memoris": "memoriz",
    "personalis": "personaliz", "specialis": "specializ", "capitalis": "capitaliz", "familiaris": "familiariz",
    "modernis": "moderniz", "normalis": "normaliz", "synchronis": "synchroniz", "stabilis": "stabiliz",
    "analys": "analyz", "paralys": "paralyz", "catalys": "catalyz",
}
_WORDS = {
    "centre": "center", "centres": "centers", "centred": "centered", "theatre": "theater", "theatres": "theaters",
    "litre": "liter", "litres": "liters", "fibre": "fiber", "fibres": "fibers", "calibre": "caliber",
    "cancelled": "canceled", "cancelling": "canceling", "travelled": "traveled", "travelling": "traveling",
    "traveller": "traveler", "travellers": "travelers", "labelled": "labeled", "labelling": "labeling",
    "modelled": "modeled", "modelling": "modeling", "levelled": "leveled", "levelling": "leveling",
    "fuelled": "fueled", "fuelling": "fueling", "signalled": "signaled", "signalling": "signaling",
    "totalled": "totaled", "jewellery": "jewelry", "programme": "program", "programmes": "programs",
    "catalogue": "catalog", "catalogues": "catalogs", "licence": "license", "licences": "licenses",
    "defence": "defense", "offence": "offense", "pretence": "pretense", "grey": "gray", "greys": "grays",
    "whilst": "while", "amongst": "among", "learnt": "learned", "spelt": "spelled", "dreamt": "dreamed",
    "aluminium": "aluminum", "tyre": "tire", "tyres": "tires", "cheque": "check", "cheques": "checks",
    "mum": "mom", "mums": "moms", "aeroplane": "airplane", "aeroplanes": "airplanes", "practise": "practice",
    "practised": "practiced", "practising": "practicing", "enrolment": "enrollment", "fulfil": "fulfill",
    "fulfilment": "fulfillment", "instalment": "installment", "judgement": "judgment", "ageing": "aging",
    "manoeuvre": "maneuver", "manoeuvres": "maneuvers", "oestrogen": "estrogen", "paediatric": "pediatric",
    "anaemia": "anemia", "orthopaedic": "orthopedic", "sceptical": "skeptical", "sceptic": "skeptic",
    "plough": "plow", "emphasise": "emphasize", "emphasised": "emphasized", "emphasises": "emphasizes", "emphasising": "emphasizing", "kerb": "curb", "pyjamas": "pajamas", "storey": "story", "storeys": "stories",
}
_ENDINGS = ("", "s", "ed", "ing", "er", "ers", "able", "ite", "ites", "ful", "less", "e", "es", "ation", "ations")
for stem, us in _STEMS.items():
    for end in _ENDINGS:
        _WORDS.setdefault(stem + end, us + end)

_PATTERN = re.compile(r"\b(" + "|".join(sorted(map(re.escape, _WORDS), key=len, reverse=True)) + r")\b", re.IGNORECASE)


def _keep_case(original, replacement):
    if original.isupper() and len(original) > 1:
        return replacement.upper()
    if original[0].isupper():
        return replacement[0].upper() + replacement[1:]
    return replacement


def americanize(text):
    """'Do me a favour, organise the colours' -> 'Do me a favor, organize the colors'."""
    if not text:
        return text
    return _PATTERN.sub(lambda m: _keep_case(m.group(0), _WORDS[m.group(0).lower()]), text)
