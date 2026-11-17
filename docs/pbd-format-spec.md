# The racial bone deformer file (`human.pbd`): format and behaviour

This is the clean-room specification for `XivLiveMesh/PbdFile.cs` (see `docs/pbd-clean-room.md`). It states the
facts of the game's file `chara/xls/boneDeformer/human.pbd` and what the public members `PbdFile` (the
constructor), `Chain`, `Resolve` and `Lists` must compute. It was written by reading the previous reader, its
tests and callers, and the game's own file. It contains no code from that reader.

The two places where the required behaviour differs from the previous reader are marked in section 3 and name the
requirement they answer. Everything else describes the previous reader's observable behaviour, which the new code
must reproduce exactly.

Terms used below:

- **race code**: the game's 16-bit gender-and-race number, written in decimal without the leading `c` and zeros
  (101 = Midlander male `c0101`, 201 = Midlander female, 901 = Roegadyn male, 1101/1201 = Lalafell male/female, and
  so on). The value 0 means "no race".
- **race entry**: one row of the first table in the file (one per race).
- **link**: one row of the second table (one node of the tree the races form).
- **block**: the per-race deformer information a race entry points at: a set of bone names, each with a matrix.
- **N**: the number of race entries, read from the first four bytes.

## 1. File layout

Every multi-byte value in the file is little-endian. Floats are IEEE-754 single precision (4 bytes).

### 1.1 The file as a whole

| Byte position | Width | Type | Meaning |
|---|---|---|---|
| 0 | 4 | signed 32-bit integer | N, the number of race entries. The link table has the same number of rows, N. |
| 4 | 12 × N | table | the race entries (section 1.2), entry *i* at byte 4 + 12·*i* |
| 4 + 12·N | 8 × N | table | the links (section 1.3), link *j* at byte 4 + 12·N + 8·*j* |
| 4 + 20·N onwards | rest of file | blocks | the deformer blocks (section 2), wherever the race entries point |

The two tables are back to back with no padding. Nothing in the file says where the blocks are except the race
entries' block offsets; a block can be anywhere in the file.

In the game's file (checked on 2026-11-16): N = 31, the file is 256 228 bytes long, the tables end at byte 624 and
the first block begins there.

### 1.2 A race entry (12 bytes)

Positions are relative to the entry's first byte (byte 4 + 12·*i* of the file).

| +offset | Width | Signedness | Meaning |
|---|---|---|---|
| +0 | 2 | unsigned 16-bit | the race code this entry describes |
| +2 | 2 | unsigned 16-bit | index of this race's row in the link table (0-based) |
| +4 | 4 | SIGNED 32-bit | byte position of this race's block, measured from the beginning of the file (byte 0) |
| +8 | 4 | (float) | not used by the reader; ignore it. In the game's file it holds a per-race float between about 0.70 and 1.95. |

A block offset that is 0, negative, or not less than the file length means "this race has no block" (section 3).
In the game's file the very first entry, race 101, has block offset 0: race 101 has no block of its own.

### 1.3 A link (8 bytes)

Positions are relative to the link's first byte (byte 4 + 12·N + 8·*j*).

| +offset | Width | Signedness | Meaning |
|---|---|---|---|
| +0 | 2 | unsigned 16-bit | index (into the link table) of this node's parent node; 65535 (0xFFFF) means "no parent" (a root) |
| +2 | 2 | unsigned 16-bit | index of the first child node, 65535 if none. Not used by the reader. |
| +4 | 2 | unsigned 16-bit | index of the next sibling node, 65535 if none. Not used by the reader. |
| +6 | 2 | unsigned 16-bit | index (into the race entry table) of the race entry this node stands for |

Note that a link's parent field names another **link**, not a race entry; the parent race is found through that
parent link's +6 field.

In the game's file the link row of an entry is not the entry's own index (for example entry 5, race 202, uses link
20, whose parent is link 4, which stands for entry 4, race 201). Link 0 stands for entry 0 (race 101) and is the root.

### 1.4 Patching a file built by `HairPortFixtures.BuildPbd`

`Proteus.Tests/HairPortFixtures.BuildPbd` writes exactly the layout above, with races in the order given (entry *i*
is the *i*-th race passed in). For a file of N races:

| Field | Byte position |
|---|---|
| N | 0 (4 bytes) |
| entry *i*: race code | 4 + 12·*i* (2 bytes) |
| entry *i*: link index | 6 + 12·*i* (2 bytes); `BuildPbd` writes *i* |
| entry *i*: block offset | 8 + 12·*i* (4 bytes, signed) |
| entry *i*: unused float | 12 + 12·*i* (4 bytes); `BuildPbd` writes 1.0 |
| link *j*: parent link | 4 + 12·N + 8·*j* (2 bytes); `BuildPbd` writes the index of the parent race's entry (which is also its link), or 65535 |
| link *j*: first child | 6 + 12·N + 8·*j* (2 bytes); `BuildPbd` writes 65535 |
| link *j*: next sibling | 8 + 12·N + 8·*j* (2 bytes); `BuildPbd` writes 65535 |
| link *j*: entry index | 10 + 12·N + 8·*j* (2 bytes); `BuildPbd` writes *j* |
| first block | 4 + 20·N; each later block follows the one before (each block is padded with zeros to a multiple of 4 bytes) |

Example: for three races (N = 3) the links begin at byte 40, the first block at byte 64; entry 1's block offset is
the 4 bytes at byte 20, entry 2's at byte 32; link 2's parent field is at byte 56.

## 2. A deformer block

All positions in this section are relative to the block's first byte, the byte its race entry's block offset
names. Call that byte the block origin. Let B be the block's bone count.

### 2.1 Layout

| Position from the block origin | Width | Type | Meaning |
|---|---|---|---|
| 0 | 4 | signed 32-bit integer | B, the number of bones in the block |
| 4 | 2 × B | table of unsigned 16-bit integers | name offset of bone *b* at 4 + 2·*b*: where bone *b*'s name begins, measured from the block origin |
| 4 + 2·B | 2 if B is odd, 0 if B is even | padding | skipped; its contents are ignored. Its size is (2·B) mod 4, so the matrices begin on a 4-byte boundary relative to the block origin |
| 4 + 2·B + pad | 48 × B | table of matrices | matrix of bone *b* at 4 + 2·B + pad + 48·*b*: 12 floats (section 2.3) |

The names themselves can be anywhere in the file; in the game's file and in `BuildPbd`'s output they follow the
matrix table. The bone order is the same in the name-offset table and the matrix table: the *b*-th name goes with
the *b*-th matrix.

### 2.2 Names

- **Name offsets are UNSIGNED 16-bit values** (0 to 65535), added to the block origin. They must not be read as
  signed: NIRÆ's deformer of about 650 bones puts names more than 32 767 bytes past the block origin, and a signed
  reading makes them negative and breaks the whole file. (`BuildPbd` writes them through a signed 16-bit write, which
  produces the same bytes; `PbdFileTests.Reads_a_deformer_whose_bone_names_sit_past_32_KB` pins this.)
- A name runs from its first byte up to, but not including, the first zero byte after it. If no zero byte follows
  before the end of the file, the name runs to the end of the file (a missing terminator on the last name is not an
  error).
- A name offset that lands exactly on the end of the file, or on a zero byte, gives the empty name "" (a valid key).
- The bytes are decoded as UTF-8. Non-ASCII bytes are decoded as UTF-8 sequences (the bytes C3 A9 become "é");
  malformed bytes become U+FFFD, the replacement character, exactly as .NET's default `Encoding.UTF8` decoder does
  (a lone 0xFF becomes one U+FFFD). Nothing is trimmed or case-folded.
- Names are compared exactly: ordinal, case-sensitive.
- **A name listed twice in one block**: the earliest occurrence (lowest *b*) keeps its matrix; later occurrences
  are ignored. The block's dictionary therefore has one key per distinct name, and its count is the number of
  distinct names.

The game's file has no duplicate names and no non-ASCII bytes in any block.

### 2.3 The 12 floats and the 4×4 transform

Number a matrix's floats f0 to f11 in file order (f0 at the matrix's first byte, the float numbered *n* at +4·*n*).

The 12 floats are the top three rows, written row by row, of a 4×4 affine transform in the column-vector
convention (point as a column, transform on the left): row 1 is f0 f1 f2 f3, row 2 is f4 f5 f6 f7, row 3 is f8 f9 f10
f11, and the implied fourth row is 0 0 0 1. The translation is therefore f3, f7, f11.

The `Matrix4x4` (System.Numerics, row vectors) the reader stores for the bone is the **transpose** of that
transform. Element by element:

| | column 1 | column 2 | column 3 | column 4 |
|---|---|---|---|---|
| row 1 | M11 = f0 | M12 = f4 | M13 = f8 | M14 = 0 |
| row 2 | M21 = f1 | M22 = f5 | M23 = f9 | M24 = 0 |
| row 3 | M31 = f2 | M32 = f6 | M33 = f10 | M34 = 0 |
| row 4 | M41 = f3 | M42 = f7 | M43 = f11 | M44 = 1 |

So the translation lands in M41, M42, M43, as System.Numerics expects. The floats are taken as stored: no
normalisation, no rounding, NaN and infinities kept as they are.

### 2.4 Which blocks are read, and when

- Every block is read when the `PbdFile` is constructed, whether or not any chain will use it. A damaged block
  anywhere in the file therefore makes the constructor fail (section 3), even if its race is never asked for.
- Only blocks whose offset is greater than 0 and less than the file length are read. Each distinct offset is read
  once.
- Two or more race entries with the same block offset all get the same dictionary: one object, shared by every chain
  that includes any of those races.
- A block with B = 0 is valid and gives an empty dictionary (which still counts as a deformer in a chain).

## 3. Bad input

The constructor either succeeds or throws; it never returns a half-built object. Once constructed, `Chain`,
`Resolve` and `Lists` never throw because of the file's contents: a damaged tree gives an empty chain instead.

**R-1 (required of every reader, from the callers).** On input it cannot read, the constructor throws only
`ArgumentException` (or a subclass of it, such as `ArgumentOutOfRangeException`), `IndexOutOfRangeException` or
`OverflowException`. It never throws `EndOfStreamException`, `InvalidDataException`, `NullReferenceException` or
`OutOfMemoryException`. `OtherRaceWearer.Build` catches only those three types (plus out-of-memory) and turns them
into a refusal; `OtherRaceWearerTests.A_damaged_deformer_is_refused_not_thrown` pins three damaged files: the bytes
01 00 00 00 00 00, the bytes FF FF FF FF 00 00 00 00, and the bytes 10 00 00 00. Every count read from the file (the
number of races, which is also the number of links, and each block's bone count) is checked against the bytes left
before anything is allocated from it.

The previous reader's failures were all `ArgumentOutOfRangeException` (reading a value whose bytes run past the end
of the file), except where noted below.

| Input | Outcome |
|---|---|
| empty file, or fewer than 4 bytes | throws `ArgumentOutOfRangeException` |
| N = 0 (a 4-byte file holding 0) | constructs; every chain is empty |
| N is negative (for example -1, bytes FF FF FF FF) | throws (see the first required change below) |
| N larger than the file can hold, for example 16 with no tables after it, or a table cut short (bytes 01 00 00 00 00 00) | throws `ArgumentOutOfRangeException` (see the first required change below) |
| the tables fit exactly and no block follows (every offset points past the end) | constructs; every chain is empty |
| a race entry's block offset is 0 | the entry has no block; constructs |
| a race entry's block offset is negative | the entry has no block; constructs |
| a race entry's block offset is equal to or greater than the file length | the entry has no block; constructs |
| a block offset inside the file but with fewer than 4 bytes left for the bone count | throws `ArgumentOutOfRangeException` |
| a block's bone count is negative | throws (see the second required change below) |
| a block's bone count needs more bytes than are left (its name-offset table, padding and matrix table do not fit before the end of the file) | throws `ArgumentOutOfRangeException` (see the second required change below) |
| a block cut short in the middle of a matrix | throws `ArgumentOutOfRangeException` |
| a name offset that points past the end of the file | throws `ArgumentOutOfRangeException` |
| a name offset that points exactly at the end of the file | constructs; that bone's name is "" |
| the last name in the file has no zero terminator | constructs; the name runs to the end of the file |
| two race entries with the same block offset | constructs; both races share one dictionary (section 2.4) |
| two race entries with the same race code | constructs; only the first of them (lowest entry index) is ever used for that race (section 4) |
| a race entry's link index is N or more | constructs; any chain that needs that entry's parent is empty |
| a link's parent index is N or more (and not 65535) | constructs; any chain that needs that parent is empty |
| a link's entry index is N or more | constructs; any chain that needs that entry is empty |
| a loop in the links (for example a root whose parent is itself) | constructs; a chain whose walk goes round the loop without reaching the source is empty (section 4) |

The constructor does not look at the links at all: link errors only show up as empty chains.

Required change from the old reader: (answers R-1, the clause that every count is checked against the bytes left
before anything is allocated, and the clause that the constructor never throws `OutOfMemoryException`) The race
count N must be checked before any table is allocated: if N is negative, or if 4 + 20·N is greater than the file
length, the constructor throws `ArgumentException` or a subclass (`ArgumentOutOfRangeException` is the natural
choice). Do the size arithmetic in 64-bit integers so a huge N cannot overflow into a small number. The old reader
allocated its tables from N first: a count of -1 threw `OverflowException` (which R-1 also allows, so either type is
acceptable for a negative N), a count of 16 in a 4-byte file allocated sixteen-row tables before failing, and a count
near 2³¹ would have attempted a multi-gigabyte allocation and failed with `OutOfMemoryException`, which R-1 forbids.
For every file the old reader accepted, the outcome is unchanged.

Required change from the old reader: (answers R-1, the same two clauses, for a block's bone count) A block's bone
count B must be checked before anything is allocated from it: if B is negative, or if the block origin + 4 + 2·B +
((2·B) mod 4) + 48·B is greater than the file length (computed in 64-bit integers), the constructor throws
`ArgumentException` or a subclass. The old reader allocated its per-block name list from B first: a negative B threw
`OverflowException` (also allowed by R-1, so either type is acceptable for a negative B), and a B near 2³¹ would have
attempted a multi-gigabyte allocation. Any block the old reader read successfully passes this check, so for every
file the old reader accepted, the outcome is unchanged.

## 4. The race chain

`Chain(from, to)` answers: which deformer blocks bend a model authored for race `from` onto a skeleton of race `to`,
and in what order. It returns a list of dictionaries (bone name to matrix). An empty list has two meanings that the
caller cannot tell apart, and both mean "draw the model as authored":

**No bending needed** (empty list, nothing in the file is consulted):
- `from` equals `to`;
- `from` is 0;
- `to` is 0.

**No chain defined** (empty list):
- `to` is not the race code of any race entry;
- a race on the way (the target, or an ancestor before the source is reached) has no block (its offset was 0,
  negative or past the end). In the game's file race 101 has no block, so every chain whose walk would include
  race 101's own block (any chain *to* 101 from another race, for instance) is empty;
- a broken link on the way: a race entry's link index of N or more, a link whose parent is 65535 (a root reached
  without meeting the source), a parent index of N or more, or a parent link whose entry index is N or more;
- the source is never reached: `from` is not an ancestor of `to` in the tree (including `from` not being in the
  file at all, or `from` being a descendant of `to`);
- a loop: N blocks have been collected without reaching the source (see "Length limit" below).

**How the tree is followed.** The chain belongs to the path in the tree from the target up towards the root, which
must meet the source. The path is made of races: the target, its parent race, that race's parent race, and so on,
until the source. The parent race of a race R is found this way: take the first race entry (lowest index) whose race
code is R; its link index names R's link; that link's parent field names the parent link; the parent link's entry
index names the parent's race entry; that entry's race code is the parent race. When the parent race is looked up in
its turn, it is again looked up by race code (first entry with that code), not by the entry index just used. This
only matters when two entries share a race code.

**What goes in the chain.** One block for every race on that path except the source: the target's block, then its
parent's, and so on, stopping before the source. The source's own block is never part of the chain (so the source
does not need a block, and race 101 having none is no obstacle to chains *from* 101). Each of these races must have
a block, found through its first race entry; if any lacks one, there is no chain.

**Length limit.** The chain can have at most N blocks. If the source has not been reached after N blocks have been
collected, there is no chain (this is what stops a loop).

**Order of application.** The returned list is ordered from the source's side to the target: element 0 is the block
of the race just below the source (the source's child on the path), and the last element is the target's own block.
For a model authored for `from`, element 0 is applied first and the last element last (section 6). Example from the
game's file: `Chain(101, 1201)` has two elements; element 0 is race 1101's block (105 bones), element 1 is race
1201's block (100 bones).

**Other facts.**
- Each call returns a new list; the dictionaries in it are the shared ones built by the constructor.
- `from` does not need a block or a valid link of its own: it only has to turn up as the race code of a parent
  entry met on the path.
- A cycle that does pass through the source still gives a chain: the walk stops as soon as it meets the source.

## 5. One bone through a chain

`Resolve(chain, bone, parentOf)` and `Lists(chain, bone, parentOf)` are static and use only their arguments (any
list of dictionaries will do, not only one from `Chain`). `parentOf` is the caller's skeleton: given a bone name it
returns the parent bone's name, or null at a root. The dictionaries' own lookup is used (for those built by the
reader, ordinal and case-sensitive).

**The bone's matrix in one deformer.** For a single dictionary D, the bone's matrix is, in order of preference:

- D's entry for `bone`, when D lists it;
- else D's entry for the nearest ancestor of `bone` that D lists, the ancestors being `parentOf(bone)`, `parentOf`
  of that, and so on until `parentOf` returns null;
- else nothing: D contributes the identity.

**Depth limit.** For each dictionary, at most 256 names are tried: the bone itself and up to 255 ancestors. If none
of those 256 is listed, that dictionary contributes nothing, as when nothing is listed at all. This also makes a `parentOf` that
loops (for example a → b → a) harmless: `Resolve` returns without error (the identity, if nothing on the loop is
listed in any dictionary) and `Lists` returns false (if nothing on the loop is listed). `parentOf` is never called
on a name once a listed name has been found in the dictionary being searched.

**Combining (`Resolve`).** The answer begins as the identity and, for each dictionary in list order, is multiplied
on the right by that dictionary's matrix for the bone (System.Numerics multiplication). So for a chain D0, D1, …, Dn
the answer is R(D0) × R(D1) × … × R(Dn), where R(Dk) is the bone's matrix in Dk (identity where it contributes
nothing). Each dictionary is searched independently: one dictionary may list the bone itself while another supplies
an ancestor's matrix, and the nearest listed ancestor may differ from one dictionary to the next.

**`Lists`.** True if at least one dictionary in the chain lists the bone or one of the (at most 255) ancestors tried
under the depth limit; false otherwise. It is true exactly when `Resolve` found some listed matrix (even if that
matrix happens to be the identity); false means `Resolve`'s answer is the identity only because the bone is unknown
everywhere.

**Empty chain.** `Resolve` returns `Matrix4x4.Identity`; `Lists` returns false. `parentOf` is never called.

## 6. Conventions

- System.Numerics treats a point as a row vector multiplied on the left: `Vector3.Transform(v, M)` computes
  v × M with the translation in M41..M43. The stored matrix (section 2.3) is the transpose of the column-vector
  transform the file holds, so `Vector3.Transform(v, M)` gives exactly the point the file's transform produces from
  v.
- Across a chain D0 … Dn, v × (R(D0) × R(D1) × … × R(Dn)) applies R(D0) to the point first and R(Dn) last. In
  column-vector terms that is Tn · … · T1 · T0 · v, where Tj is the file's transform for chain element j.

**Worked example (the game's file).** Race 901 (Roegadyn male) is entry 17; its block offset is 100 112. Its block
has B = 105, so the padding is 2 bytes and the matrix table begins at 100 112 + 4 + 210 + 2 = 100 328. Bone 64 of
the block is `j_kosi` (the pelvis): its name offset is 5898, so the name is at byte 106 010; its matrix is at
100 328 + 64 × 48 = 103 400. The 12 stored floats, in file order, are:

| f0 | f1 | f2 | f3 | f4 | f5 | f6 | f7 | f8 | f9 | f10 | f11 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1.7347636 | -5.5488353E-12 | 2.881622E-09 | 5.974819E-12 | -2.1072102E-10 | 1.1617242 | 0.12358289 | -0.015558777 | -2.8209028E-09 | 0.1235833 | 1.7000669 | -0.1330708 |

The resulting `Matrix4x4` (6 decimal places; values below 5e-7 shown as 0):

| | col 1 | col 2 | col 3 | col 4 |
|---|---|---|---|---|
| row 1 | 1.734764 | 0.000000 | 0.000000 | 0 |
| row 2 | 0.000000 | 1.161724 | 0.123583 | 0 |
| row 3 | 0.000000 | 0.123583 | 1.700067 | 0 |
| row 4 | 0.000000 | -0.015559 | -0.133071 | 1 |

`Chain(101, 901)` has exactly this one block, so `Resolve(Chain(101, 901), "j_kosi", any lookup)` is this matrix.
Applied to the point (0.1, 1.0, 0.05) with `Vector3.Transform`:

- x′ = 0.1 × 1.734764 + 1.0 × 0 + 0.05 × 0 + 0 = 0.173476
- y′ = 0.1 × 0 + 1.0 × 1.161724 + 0.05 × 0.123583 + (-0.015559) = 1.152345
- z′ = 0.1 × 0 + 1.0 × 0.123583 + 0.05 × 1.700067 + (-0.133071) = 0.075516

So the point moves to (0.173476, 1.152345, 0.075516).

## 7. Test vectors

All from the game's `chara/xls/boneDeformer/human.pbd` as installed on 2026-11-16 (256 228 bytes, N = 31).

**Comparison rule.** Matrix values are given to 6 decimal places. A matrix matches if every one of its 16 elements is
within an absolute tolerance of 1e-5 of the value given. (A value written 0.000000 may be a tiny non-zero number
such as -2.8e-9, or a negative zero; both match.)

### 7.1 Chain lengths

| `from` | `to` | Chain length | Why |
|---|---|---|---|
| 101 | 101 | 0 | same race: no bending |
| 101 | 0 | 0 | a race 0: no bending |
| 0 | 201 | 0 | a race 0: no bending |
| 101 | 9999 | 0 | `to` not in the file |
| 9999 | 201 | 0 | `from` not in the file: the source is never reached |
| 201 | 101 | 0 | race 101 has no block (and 201 is not its ancestor) |
| 1201 | 101 | 0 | as above |
| 102 | 201 | 0 | 102 is not an ancestor of 201: the source is never reached |
| 101 | 901 | 1 | one step (block sizes: 105) |
| 101 | 201 | 1 | cross-sex, one step (block sizes: 103) |
| 101 | 1201 | 2 | two steps: 1101's block then 1201's (block sizes: 105, 100) |
| 101 | 204 | 2 | two steps: 104's block then 204's (block sizes: 105, 103) |
| 101 | 202 | 2 | two steps |
| 104 | 204 | 1 | one step from a non-root source (block size: 103) |
| 201 | 1801 | 1 | one step from a non-root source (block size: 100) |

("Block sizes" are the `Count` of each dictionary in the returned list, in order.)

### 7.2 The parent lookup used by the matrix cases

The matrix cases use this `parentOf`, and nothing else from any skeleton. Any name not in the table has no parent
(null).

| Bone | Parent |
|---|---|
| n_root | (none) |
| n_hara | n_root |
| j_kosi | n_hara |
| j_sebo_a | j_kosi |
| j_sebo_b | j_sebo_a |
| j_sebo_c | j_sebo_b |
| j_kubi | j_sebo_c |
| j_kao | j_kubi |
| j_mune_l | j_sebo_b |
| x_probe_child | j_kao |
| x_probe_orphan | (none) |

`x_probe_child` and `x_probe_orphan` are made-up names that no block lists.

### 7.3 Resolved matrices

Each case gives `Resolve(Chain(from, to), bone, parentOf)` as its four rows (M11 M12 M13 M14 / M21 … / M31 … /
M41 …), and `Lists(Chain(from, to), bone, parentOf)`. "Listed in" says, per chain element, whether that dictionary
lists the bone itself.

**Case A: listed directly, one deformer.** `from` 101, `to` 901, bone `j_kosi`. Listed in: yes. `Lists`: true.

| | | | |
|---|---|---|---|
| 1.734764 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 1.161724 | 0.123583 | 0.000000 |
| 0.000000 | 0.123583 | 1.700067 | 0.000000 |
| 0.000000 | -0.015559 | -0.133071 | 1.000000 |

**Case B: listed directly in both blocks of a two-step chain.** `from` 101, `to` 1201, bone `j_kosi`. Listed
in: yes, yes. `Lists`: true.

| | | | |
|---|---|---|---|
| 0.791213 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 0.599831 | 0.049041 | 0.000000 |
| 0.000000 | 0.049041 | 0.813460 | 0.000000 |
| 0.000000 | -0.212461 | -0.071442 | 1.000000 |

**Case C: cross-sex, listed directly.** `from` 101, `to` 201, bone `j_kao`. Listed in: yes. `Lists`: true.

| | | | |
|---|---|---|---|
| 1.001208 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 1.010835 | 0.000000 | 0.000000 |
| 0.000000 | 0.000000 | 1.012414 | 0.000000 |
| 0.000000 | -0.133022 | -0.004906 | 1.000000 |

**Case D: listed directly in the first deformer, found through an ancestor in the second.** `from` 101, `to` 204,
bone `j_mune_l`. Listed in: yes (104's block), no (204's block, which lists its ancestor `j_sebo_b`). `Lists`: true.
The answer is 104's `j_mune_l` matrix times 204's `j_sebo_b` matrix.

| | | | |
|---|---|---|---|
| 0.716123 | 0.002477 | 0.026286 | 0.000000 |
| 0.002152 | 0.730797 | -0.000681 | 0.000000 |
| 0.026315 | 0.018558 | 0.822960 | 0.000000 |
| -0.002695 | 0.093284 | -0.001429 | 1.000000 |

**Case E: found only through an ancestor, in every deformer.** `from` 101, `to` 1201, bone `x_probe_child` (its
parent `j_kao` is listed in both blocks). Listed in: no, no. `Lists`: true. The same matrix as
`Resolve(Chain(101, 1201), "j_kao", parentOf)`.

| | | | |
|---|---|---|---|
| 1.120343 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 1.120344 | 0.000000 | 0.000000 |
| 0.000000 | 0.000000 | 1.120345 | 0.000000 |
| 0.000000 | -1.047983 | -0.024068 | 1.000000 |

**Case F: found only through an ancestor, one deformer.** `from` 101, `to` 901, bone `x_probe_child`. Listed in:
no. `Lists`: true. The same matrix as 901's `j_kao`.

| | | | |
|---|---|---|---|
| 1.370024 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 1.197100 | 0.000000 | 0.000000 |
| 0.000000 | 0.000000 | 1.385931 | 0.000000 |
| 0.000000 | 0.126864 | -0.006652 | 1.000000 |

**Case G: nothing listed.** `from` 101, `to` 1201, bone `x_probe_orphan` (no parent). Listed in: no, no. `Lists`:
false. The identity. (The same holds for `from` 101, `to` 901.)

| | | | |
|---|---|---|---|
| 1.000000 | 0.000000 | 0.000000 | 0.000000 |
| 0.000000 | 1.000000 | 0.000000 | 0.000000 |
| 0.000000 | 0.000000 | 1.000000 | 0.000000 |
| 0.000000 | 0.000000 | 0.000000 | 1.000000 |

**Case H: an empty chain.** `from` 201, `to` 101 (length 0), bone `j_kosi`: `Resolve` gives the identity,
`Lists` false.

**Case I: a looping `parentOf`.** `from` 101, `to` 901, bone `x_a`, with a lookup where `x_a`'s parent is `x_b` and
`x_b`'s parent is `x_a` (neither listed): `Resolve` returns the identity without hanging or throwing; `Lists` is
false.

### 7.4 Synthetic checks with `BuildPbd`

These need no game files. Build three races with `HairPortFixtures.BuildPbd`: race 101 (no parent) listing `j_kao`;
race 201 (parent 101) listing `j_kao` and `j_kosi`; race 301 (parent 201) listing `j_kao`. N = 3, the links begin at
byte 40, the blocks at byte 64, and entry 1's block offset is at byte 20.

| Change to the file | `Chain(101,201)` | `Chain(101,301)` | `Chain(201,301)` | `Chain(301,101)` |
|---|---|---|---|---|
| none | 1 | 2 | 1 | 0 |
| entry 1's block offset set to 0, to -5, or to the file length | 0 | 0 | 1 | 0 |
| entry 2's block offset set to entry 1's (shared block) | 1 | 2 | 1 (the shared block, 2 bones) | 0 |
| link 2's parent set to 7 | 1 | 0 | 0 | 0 |
| link 1's entry index set to 9 | 1 | 0 | 0 | 0 |
| entry 2's link index set to 9 | 1 | 0 | 0 | 0 |
| link 0's parent set to 0 (the root is its own parent) | 1 | 2 | 1 | 0 |
| link 0's parent set to 2 (a cycle 101 → 301 → 201 → 101) | 1 | 2 | 1 | 1 |
| entry 1's race code set to 301 (two entries for 301) | 0 | 1 | 0 | 0 |

The unchanged file is 312 bytes, with block offsets 64, 128 and 248. Throws (an R-1 type): the file cut to 276
bytes, 28 bytes into entry 2's one-bone block (20 bytes into its matrix); entry
1's block offset set to the file length minus 2; entry 1's bone count set to 1 000 000; entry 1's first name offset
set to 60000. Constructs, with that bone's name "": entry 1's first name offset set so it points exactly at the end
of the file. Constructs, with the name `j_kao` intact: the file with its trailing zero bytes removed, so the last
name has no terminator.
