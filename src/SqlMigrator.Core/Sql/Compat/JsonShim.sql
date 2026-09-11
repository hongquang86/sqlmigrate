/* ============================================================================
   Thư viện tương thích JSON cho SQL Server 2014 (mô phỏng tối thiểu JSON 2016).
   - Chỉ dùng cú pháp có từ SQL 2014 trở xuống (không dùng hàm/tính năng của 2016).
   - Chạy lại nhiều lần an toàn (DROP + CREATE theo đúng thứ tự phụ thuộc).
   - Phạm vi mô phỏng: object, array, string (kể cả escape \uXXXX), number,
     true/false/null; đường dẫn $.a, $.a.b, $.a[0], $[0], ['tên có dấu cách'].
   - So khớp tên thuộc tính phân biệt hoa/thường đúng chuẩn JSON.
   - KHÔNG dùng cho tài liệu JSON hàng trăm MB (parser duyệt từng ký tự sẽ chậm).
   ============================================================================ */
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Compat')
    EXEC('CREATE SCHEMA Compat');
GO

/* Xóa theo thứ tự ngược phụ thuộc để deploy lại sạch. */
IF OBJECT_ID(N'Compat.OpenJson', N'TF') IS NOT NULL DROP FUNCTION Compat.OpenJson;
GO
IF OBJECT_ID(N'Compat.JsonValue', N'FN') IS NOT NULL DROP FUNCTION Compat.JsonValue;
GO
IF OBJECT_ID(N'Compat.JsonQuery', N'FN') IS NOT NULL DROP FUNCTION Compat.JsonQuery;
GO
IF OBJECT_ID(N'Compat.IsJson', N'FN') IS NOT NULL DROP FUNCTION Compat.IsJson;
GO
IF OBJECT_ID(N'Compat._JsonLocate', N'TF') IS NOT NULL DROP FUNCTION Compat._JsonLocate;
GO
IF OBJECT_ID(N'Compat._JsonValid', N'FN') IS NOT NULL DROP FUNCTION Compat._JsonValid;
GO
IF OBJECT_ID(N'Compat._JsonTokens', N'TF') IS NOT NULL DROP FUNCTION Compat._JsonTokens;
GO
IF OBJECT_ID(N'Compat._JsonUnescape', N'FN') IS NOT NULL DROP FUNCTION Compat._JsonUnescape;
GO
IF OBJECT_ID(N'Compat.JsonEscape', N'FN') IS NOT NULL DROP FUNCTION Compat.JsonEscape;
GO

/* ----------------------------------------------------------------------------
   Compat._JsonUnescape: bỏ escape chuỗi JSON (đầu vào đã cắt bỏ 2 dấu nháy).
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat._JsonUnescape(@s NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
AS
BEGIN
    IF @s IS NULL RETURN NULL;
    DECLARE @o NVARCHAR(MAX) = N'';
    -- DATALENGTH (không phải LEN) để giữ đúng khoảng trắng ở cuối chuỗi.
    DECLARE @n INT = DATALENGTH(@s) / 2;
    DECLARE @i INT = 1;
    WHILE @i <= @n
    BEGIN
        DECLARE @c NCHAR(1) = SUBSTRING(@s, @i, 1);
        IF @c <> N'\' OR @i = @n
        BEGIN SET @o = @o + @c; SET @i = @i + 1; CONTINUE; END
        DECLARE @e NCHAR(1) = SUBSTRING(@s, @i + 1, 1);
        IF @e = N'"' BEGIN SET @o = @o + N'"'; SET @i = @i + 2; CONTINUE; END
        IF @e = N'\' BEGIN SET @o = @o + N'\'; SET @i = @i + 2; CONTINUE; END
        IF @e = N'/' BEGIN SET @o = @o + N'/'; SET @i = @i + 2; CONTINUE; END
        IF @e = N'b' BEGIN SET @o = @o + NCHAR(8); SET @i = @i + 2; CONTINUE; END
        IF @e = N'f' BEGIN SET @o = @o + NCHAR(12); SET @i = @i + 2; CONTINUE; END
        IF @e = N'n' BEGIN SET @o = @o + NCHAR(10); SET @i = @i + 2; CONTINUE; END
        IF @e = N'r' BEGIN SET @o = @o + NCHAR(13); SET @i = @i + 2; CONTINUE; END
        IF @e = N't' BEGIN SET @o = @o + NCHAR(9); SET @i = @i + 2; CONTINUE; END
        IF @e = N'u' AND @i + 5 <= @n
        BEGIN
            DECLARE @hex NVARCHAR(4) = SUBSTRING(@s, @i + 2, 4);
            IF @hex NOT LIKE N'%[^0-9a-fA-F]%'
            BEGIN
                SET @o = @o + NCHAR(CONVERT(INT, CONVERT(VARBINARY(2), N'0x' + @hex, 1)));
                SET @i = @i + 6; CONTINUE;
            END
        END
        -- Escape lạ: giữ nguyên ký tự (khoan dung, không ném lỗi ở đây).
        SET @o = @o + @e; SET @i = @i + 2;
    END
    RETURN @o;
END
GO

/* ----------------------------------------------------------------------------
   Compat._JsonTokens: tách tài liệu JSON thành token phẳng theo thứ tự.
   kind: '{','}','[',']',':',',','S'(chuỗi),'N'(số),'T'(true),'F'(false),'U'(null).
   Dòng kind='!' là cờ lỗi (TVF không RAISERROR được): depth = -(mã lỗi).
   depth: token gốc depth 1; con trực tiếp của node depth d có depth d+1.
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat._JsonTokens(@json NVARCHAR(MAX))
RETURNS @t TABLE(seq INT IDENTITY(1,1) PRIMARY KEY, kind NCHAR(1), pos INT, len INT, depth INT)
AS
BEGIN
    DECLARE @err INT = 0;
    DECLARE @n INT = LEN(ISNULL(@json, N''));
    DECLARE @i INT = 1;
    DECLARE @depth INT = 0;
    WHILE @i <= @n AND @err = 0
    BEGIN
        DECLARE @c NCHAR(1) = SUBSTRING(@json, @i, 1);
        -- Bỏ qua khoảng trắng JSON (cách, tab, CR, LF).
        IF @c = N' ' OR @c = NCHAR(9) OR @c = NCHAR(10) OR @c = NCHAR(13)
        BEGIN SET @i = @i + 1; CONTINUE; END
        IF @c = N'{' OR @c = N'['
        BEGIN
            SET @depth = @depth + 1;
            IF @depth > 100 BEGIN SET @err = 3; BREAK; END
            INSERT INTO @t(kind, pos, len, depth) VALUES(@c, @i, 1, @depth);
            SET @i = @i + 1; CONTINUE;
        END
        IF @c = N'}' OR @c = N']' OR @c = N':' OR @c = N','
        BEGIN
            INSERT INTO @t(kind, pos, len, depth) VALUES(@c, @i, 1, @depth);
            IF @c = N'}' OR @c = N']'
            BEGIN
                SET @depth = @depth - 1;
                IF @depth < 0 BEGIN SET @err = 4; BREAK; END
            END
            SET @i = @i + 1; CONTINUE;
        END
        IF @c = N'"'
        BEGIN
            DECLARE @j INT = @i + 1;
            DECLARE @closed BIT = 0;
            WHILE @j <= @n AND @err = 0
            BEGIN
                DECLARE @d NCHAR(1) = SUBSTRING(@json, @j, 1);
                IF @d = N'\'
                BEGIN
                    IF @j + 1 > @n BEGIN BREAK; END
                    DECLARE @e NCHAR(1) = SUBSTRING(@json, @j + 1, 1);
                    IF @e = N'u'
                    BEGIN
                        IF @j + 5 > @n BEGIN BREAK; END
                        IF SUBSTRING(@json, @j + 2, 4) LIKE N'%[^0-9a-fA-F]%' BEGIN SET @err = 5; BREAK; END
                        SET @j = @j + 6; CONTINUE;
                    END
                    IF CHARINDEX(@e, N'"/\bfnrt') = 0 BEGIN SET @err = 5; BREAK; END
                    SET @j = @j + 2; CONTINUE;
                END
                IF @d = N'"' BEGIN SET @closed = 1; BREAK; END
                -- Ký tự điều khiển thô trong chuỗi là sai chuẩn JSON.
                IF UNICODE(@d) < 32 BEGIN SET @err = 5; BREAK; END
                SET @j = @j + 1;
            END
            IF @err <> 0 BREAK;
            IF @closed = 0 BEGIN SET @err = 6; BREAK; END
            INSERT INTO @t(kind, pos, len, depth) VALUES(N'S', @i, @j - @i + 1, @depth + 1);
            SET @i = @j + 1; CONTINUE;
        END
        IF @c = N't' OR @c = N'f' OR @c = N'n'
        BEGIN
            DECLARE @word NVARCHAR(5);
            DECLARE @wlen INT;
            DECLARE @wkind NCHAR(1);
            IF @c = N't' BEGIN SET @word = N'true'; SET @wlen = 4; SET @wkind = N'T'; END
            ELSE IF @c = N'f' BEGIN SET @word = N'false'; SET @wlen = 5; SET @wkind = N'F'; END
            ELSE BEGIN SET @word = N'null'; SET @wlen = 4; SET @wkind = N'U'; END
            IF SUBSTRING(@json, @i, @wlen) <> @word BEGIN SET @err = 7; BREAK; END
            INSERT INTO @t(kind, pos, len, depth) VALUES(@wkind, @i, @wlen, @depth + 1);
            SET @i = @i + @wlen; CONTINUE;
        END
        IF @c = N'-' OR (@c >= N'0' AND @c <= N'9')
        BEGIN
            DECLARE @k INT = @i;
            IF SUBSTRING(@json, @k, 1) = N'-' SET @k = @k + 1;
            IF @k > @n BEGIN SET @err = 8; BREAK; END
            DECLARE @g NCHAR(1) = SUBSTRING(@json, @k, 1);
            IF @g = N'0' SET @k = @k + 1;
            ELSE IF @g >= N'1' AND @g <= N'9'
            BEGIN
                WHILE @k <= @n AND SUBSTRING(@json, @k, 1) >= N'0' AND SUBSTRING(@json, @k, 1) <= N'9'
                    SET @k = @k + 1;
            END
            ELSE BEGIN SET @err = 8; BREAK; END
            -- Phần thập phân (bắt buộc ít nhất 1 chữ số sau dấu chấm).
            IF @k <= @n AND SUBSTRING(@json, @k, 1) = N'.'
            BEGIN
                SET @k = @k + 1;
                DECLARE @frac INT = 0;
                WHILE @k <= @n AND SUBSTRING(@json, @k, 1) >= N'0' AND SUBSTRING(@json, @k, 1) <= N'9'
                BEGIN SET @k = @k + 1; SET @frac = @frac + 1; END
                IF @frac = 0 BEGIN SET @err = 8; BREAK; END
            END
            -- Phần mũ e/E (bắt buộc ít nhất 1 chữ số).
            IF @k <= @n AND (SUBSTRING(@json, @k, 1) = N'e' OR SUBSTRING(@json, @k, 1) = N'E')
            BEGIN
                SET @k = @k + 1;
                IF @k <= @n AND (SUBSTRING(@json, @k, 1) = N'+' OR SUBSTRING(@json, @k, 1) = N'-')
                    SET @k = @k + 1;
                DECLARE @expo INT = 0;
                WHILE @k <= @n AND SUBSTRING(@json, @k, 1) >= N'0' AND SUBSTRING(@json, @k, 1) <= N'9'
                BEGIN SET @k = @k + 1; SET @expo = @expo + 1; END
                IF @expo = 0 BEGIN SET @err = 8; BREAK; END
            END
            INSERT INTO @t(kind, pos, len, depth) VALUES(N'N', @i, @k - @i, @depth + 1);
            SET @i = @k; CONTINUE;
        END
        -- Ký tự lạ ngoài chuẩn JSON.
        SET @err = 9;
    END
    -- Mất cân bằng đóng/mở.
    IF @err = 0 AND @depth <> 0 SET @err = 10;
    IF @err <> 0 INSERT INTO @t(kind, pos, len, depth) VALUES(N'!', 0, 0, -@err);
    RETURN;
END
GO

/* ----------------------------------------------------------------------------
   Compat._JsonValid: kiểm tra ngữ pháp JSON trên dãy token (máy trạng thái).
   Trả 1 khi hợp lệ, 0 trong mọi trường hợp còn lại.
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat._JsonValid(@json NVARCHAR(MAX))
RETURNS BIT
AS
BEGIN
    DECLARE @tk TABLE(seq INT IDENTITY(1,1) PRIMARY KEY, kind NCHAR(1));
    INSERT INTO @tk(kind)
        SELECT kind FROM Compat._JsonTokens(@json) ORDER BY seq;
    -- Cờ lỗi tokenizer hoặc tài liệu rỗng.
    IF EXISTS (SELECT 1 FROM @tk WHERE kind = N'!') RETURN 0;
    DECLARE @n INT; SELECT @n = COUNT(*) FROM @tk;
    IF @n = 0 RETURN 0;

    DECLARE @stack VARCHAR(128) = '';
    DECLARE @mode NCHAR(1) = 'V'; -- V: chờ giá trị; K: chờ tên thuộc tính/đóng; C: chờ ':'; E: chờ ',', đóng hoặc hết.
    DECLARE @roots INT = 0;
    DECLARE @ok BIT = 1;
    DECLARE @s INT = 1;
    DECLARE @k NCHAR(1);
    WHILE @s <= @n AND @ok = 1
    BEGIN
        SELECT @k = kind FROM @tk WHERE seq = @s;
        IF @mode = 'V'
        BEGIN
            IF @k = '{' BEGIN IF @stack = '' SET @roots = @roots + 1; SET @stack = @stack + '}'; SET @mode = 'K'; END
            ELSE IF @k = '[' BEGIN IF @stack = '' SET @roots = @roots + 1; SET @stack = @stack + ']'; SET @mode = 'V'; END
            ELSE IF @k = 'S' OR @k = 'N' OR @k = 'T' OR @k = 'F' OR @k = 'U'
            BEGIN IF @stack = '' SET @roots = @roots + 1; SET @mode = 'E'; END
            ELSE SET @ok = 0;
        END
        ELSE IF @mode = 'K'
        BEGIN
            IF @k = '}' AND RIGHT(@stack, 1) = '}'
            BEGIN SET @stack = LEFT(@stack, LEN(@stack) - 1); SET @mode = 'E'; END
            ELSE IF @k = 'S' SET @mode = 'C';
            ELSE SET @ok = 0;
        END
        ELSE IF @mode = 'C'
        BEGIN
            IF @k = ':' SET @mode = 'V'; ELSE SET @ok = 0;
        END
        ELSE IF @mode = 'E'
        BEGIN
            IF @k = ',' AND LEN(@stack) > 0
            BEGIN IF RIGHT(@stack, 1) = ']' SET @mode = 'V'; ELSE SET @mode = 'K'; END
            ELSE IF @k = ']' AND RIGHT(@stack, 1) = ']'
            BEGIN SET @stack = LEFT(@stack, LEN(@stack) - 1); SET @mode = 'E'; END
            ELSE IF @k = '}' AND RIGHT(@stack, 1) = '}'
            BEGIN SET @stack = LEFT(@stack, LEN(@stack) - 1); SET @mode = 'E'; END
            ELSE SET @ok = 0;
        END
        SET @s = @s + 1;
    END
    IF @ok = 1 AND (@stack <> '' OR @mode <> 'E' OR @roots <> 1) SET @ok = 0;
    RETURN @ok;
END
GO

/* ----------------------------------------------------------------------------
   Compat._JsonLocate: đi theo đường dẫn JSON, trả đúng 1 dòng
   (pos, len, depth, kind, found). found=0 khi đường dẫn sai/không tồn tại.
   Đường dẫn hỗ trợ: $.a, $.a.b, $.a[0], $[0], ['tên có dấu cách'], $.
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat._JsonLocate(@json NVARCHAR(MAX), @path NVARCHAR(4000))
RETURNS @r TABLE(pos INT, len INT, depth INT, kind NCHAR(1), found BIT)
AS
BEGIN
    DECLARE @bad BIT = 0;
    -- Tokenize một lần vào biến bảng (giữ seq gốc).
    DECLARE @tk TABLE(seq INT IDENTITY(1,1) PRIMARY KEY, kind NCHAR(1), pos INT, len INT, depth INT);
    INSERT INTO @tk(kind, pos, len, depth)
        SELECT kind, pos, len, depth FROM Compat._JsonTokens(@json) ORDER BY seq;
    IF EXISTS (SELECT 1 FROM @tk WHERE kind = N'!') SET @bad = 1;

    -- Tách đường dẫn thành bước (P=tên thuộc tính, I=chỉ số mảng).
    DECLARE @steps TABLE(ord INT IDENTITY(1,1) PRIMARY KEY, isIndex BIT, name NVARCHAR(4000), idx INT);
    IF @bad = 0 AND (@path IS NULL OR LEN(@path) = 0 OR SUBSTRING(@path, 1, 1) <> N'$')
        SET @bad = 1;
    IF @bad = 0
    BEGIN
        DECLARE @p INT = 2;
        DECLARE @plen INT = LEN(@path);
        WHILE @p <= @plen AND @bad = 0
        BEGIN
            DECLARE @ch NCHAR(1) = SUBSTRING(@path, @p, 1);
            IF @ch = N'.'
            BEGIN
                SET @p = @p + 1;
                DECLARE @start INT = @p;
                WHILE @p <= @plen AND SUBSTRING(@path, @p, 1) <> N'.' AND SUBSTRING(@path, @p, 1) <> N'['
                    SET @p = @p + 1;
                IF @p = @start BEGIN SET @bad = 1; BREAK; END
                INSERT INTO @steps(isIndex, name, idx)
                    VALUES(0, SUBSTRING(@path, @start, @p - @start), 0);
            END
            ELSE IF @ch = N'['
            BEGIN
                DECLARE @q INT = CHARINDEX(N']', @path, @p + 1);
                IF @q = 0 BEGIN SET @bad = 1; BREAK; END
                DECLARE @inner NVARCHAR(4000) = SUBSTRING(@path, @p + 1, @q - @p - 1);
                -- Ngoặc rỗng hoặc số quá lớn đều là đường dẫn sai.
                IF @inner = N'' BEGIN SET @bad = 1; BREAK; END
                IF @inner LIKE N'%[^0-9]%'
                BEGIN
                    IF LEN(@inner) < 2 OR SUBSTRING(@inner, 1, 1) <> N''''
                       OR RIGHT(@inner, 1) <> N''''
                    BEGIN SET @bad = 1; BREAK; END
                    INSERT INTO @steps(isIndex, name, idx)
                        VALUES(0, REPLACE(SUBSTRING(@inner, 2, LEN(@inner) - 2), N'''''', N''''), 0);
                END
                ELSE
                BEGIN
                    DECLARE @tryIdx INT = TRY_CONVERT(INT, @inner);
                    IF @tryIdx IS NULL BEGIN SET @bad = 1; BREAK; END
                    INSERT INTO @steps(isIndex, name, idx) VALUES(1, N'', @tryIdx);
                END
                SET @p = @q + 1;
            END
            ELSE BEGIN SET @bad = 1; BREAK; END
        END
    END

    -- @cpos khởi đầu NULL để phát hiện tài liệu rỗng (giữ 0 sẽ nhầm thành "tìm thấy").
    DECLARE @cpos INT = NULL, @clen INT = NULL, @cdepth INT = NULL;
    DECLARE @ckind NCHAR(1) = NULL;
    DECLARE @cfound BIT = 0;
    IF @bad = 0
    BEGIN
        -- Node gốc = token đầu tiên.
        SELECT TOP 1 @cpos = pos, @clen = len, @cdepth = depth, @ckind = kind
          FROM @tk ORDER BY seq;
        IF @cpos IS NULL SET @bad = 1; ELSE SET @cfound = 1;

        DECLARE @ord INT = 1;
        DECLARE @ords INT; SELECT @ords = COUNT(*) FROM @steps;
        WHILE @ord <= @ords AND @bad = 0
        BEGIN
            DECLARE @isI BIT, @nm NVARCHAR(4000), @ix INT;
            SELECT @isI = isIndex, @nm = name, @ix = idx FROM @steps WHERE ord = @ord;
            -- Biên node hiện tại (dựa trên dấu đóng cùng depth).
            DECLARE @cend INT = @cpos + @clen;
            IF @ckind = N'{' OR @ckind = N'['
            BEGIN
                SELECT TOP 1 @cend = pos + len FROM @tk
                 WHERE seq > (SELECT seq FROM @tk WHERE pos = @cpos AND depth = @cdepth AND kind = @ckind)
                   AND depth = @cdepth AND (kind = N'}' OR kind = N']')
                 ORDER BY seq;
                IF @cend IS NULL BEGIN SET @bad = 1; BREAK; END
            END
            IF @isI = 0 AND @ckind = N'{'
            BEGIN
                -- Tìm thuộc tính con trùng tên (so khớp phân biệt hoa/thường).
                -- Bỏ qua giá trị chuỗi trùng tên nhờ kiểm tra dấu ':' ngay sau.
                DECLARE @hit INT = 0;
                DECLARE @cand INT;
                DECLARE @cands TABLE(s INT PRIMARY KEY);
                INSERT INTO @cands(s)
                    SELECT seq FROM @tk
                     WHERE pos > @cpos AND pos + len < @cend AND depth = @cdepth + 1 AND kind = N'S'
                     ORDER BY seq;
                DECLARE @cs INT;
                DECLARE @foundProp BIT = 0;
                SELECT TOP 1 @cs = s FROM @cands ORDER BY s;
                WHILE @cs IS NOT NULL AND @foundProp = 0
                BEGIN
                    DECLARE @cname NVARCHAR(MAX);
                    SELECT @cname = Compat._JsonUnescape(SUBSTRING(@json, pos + 1, len - 2))
                      FROM @tk WHERE seq = @cs;
                    IF @cname IS NOT NULL
                       AND @cname COLLATE Latin1_General_BIN2 = @nm COLLATE Latin1_General_BIN2
                    BEGIN
                        DECLARE @colon INT;
                        SELECT TOP 1 @colon = seq FROM @tk WHERE seq > @cs ORDER BY seq;
                        DECLARE @colonKind NCHAR(1);
                        SELECT @colonKind = kind FROM @tk WHERE seq = @colon;
                        IF @colonKind = N':'
                        BEGIN
                            SELECT TOP 1 @cpos = pos, @clen = len, @cdepth = depth, @ckind = kind
                              FROM @tk WHERE seq > @colon ORDER BY seq;
                            SET @foundProp = 1;
                        END
                    END
                    DELETE FROM @cands WHERE s = @cs;
                    SELECT TOP 1 @cs = s FROM @cands ORDER BY s;
                    IF @@ROWCOUNT = 0 SET @cs = NULL;
                END
                IF @foundProp = 0 BEGIN SET @bad = 1; BREAK; END
            END
            ELSE IF @isI = 1 AND @ckind = N'['
            BEGIN
                -- Phần tử thứ @ix (đếm từ 0) trong mảng.
                -- Reset @esk trước SELECT để lần lặp trước không rò rỉ sang lần này.
                DECLARE @esk INT = NULL;
                SELECT @esk = MIN(seq) FROM (
                    SELECT seq, ROW_NUMBER() OVER (ORDER BY seq) - 1 AS rn FROM @tk
                     WHERE pos > @cpos AND pos + len < @cend AND depth = @cdepth + 1
                       AND kind IN (N'S', N'N', N'T', N'F', N'U', N'{', N'[')
                ) AS v WHERE rn = @ix;
                IF @esk IS NULL BEGIN SET @bad = 1; BREAK; END
                SELECT @cpos = pos, @clen = len, @cdepth = depth, @ckind = kind
                  FROM @tk WHERE seq = @esk;
            END
            ELSE BEGIN SET @bad = 1; BREAK; END
            SET @ord = @ord + 1;
        END
    END

    IF @bad = 1 INSERT INTO @r(pos, len, depth, kind, found) VALUES(0, 0, 0, N' ', 0);
    ELSE INSERT INTO @r(pos, len, depth, kind, found) VALUES(@cpos, @clen, @cdepth, @ckind, @cfound);
    RETURN;
END
GO

/* ----------------------------------------------------------------------------
   Compat.JsonValue(@json, @path): mô phỏng JSON_VALUE (chỉ trả giá trị vô hướng,
   object/array trả NULL đúng semantics lax).
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat.JsonValue(@json NVARCHAR(MAX), @path NVARCHAR(4000))
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @p INT, @l INT, @d INT, @k NCHAR(1), @f BIT;
    SELECT @p = pos, @l = len, @d = depth, @k = kind, @f = found
      FROM Compat._JsonLocate(@json, @path);
    IF @f = 0 OR @f IS NULL RETURN NULL;
    IF @k = N'S' RETURN Compat._JsonUnescape(SUBSTRING(@json, @p + 1, @l - 2));
    IF @k = N'N' OR @k = N'T' OR @k = N'F' RETURN SUBSTRING(@json, @p, @l);
    IF @k = N'U' RETURN NULL;
    RETURN NULL;
END
GO

/* ----------------------------------------------------------------------------
   Compat.JsonQuery(@json, @path): mô phỏng JSON_QUERY (chỉ trả object/array,
   vô hướng trả NULL).
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat.JsonQuery(@json NVARCHAR(MAX), @path NVARCHAR(4000))
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @p INT, @l INT, @d INT, @k NCHAR(1), @f BIT;
    SELECT @p = pos, @l = len, @d = depth, @k = kind, @f = found
      FROM Compat._JsonLocate(@json, @path);
    IF @f = 0 OR @f IS NULL RETURN NULL;
    IF @k = N'{' OR @k = N'[' RETURN SUBSTRING(@json, @p, @l);
    RETURN NULL;
END
GO

/* ----------------------------------------------------------------------------
   Compat.IsJson(@json): mô phỏng ISJSON (không đối số thứ hai).
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat.IsJson(@json NVARCHAR(MAX))
RETURNS BIT
AS
BEGIN
    RETURN Compat._JsonValid(@json);
END
GO

/* ----------------------------------------------------------------------------
   Compat.OpenJson(@json[, @path]): mô phỏng OPENJSON lược đồ mặc định
   (key, value, type). type: 0=null, 1=chuỗi, 2=số, 3=boolean, 4=array, 5=object.
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat.OpenJson(@json NVARCHAR(MAX), @path NVARCHAR(4000) = N'$')
RETURNS @r TABLE([key] NVARCHAR(4000), value NVARCHAR(MAX), type INT)
AS
BEGIN
    DECLARE @p INT, @l INT, @d INT, @k NCHAR(1), @f BIT;
    SELECT @p = pos, @l = len, @d = depth, @k = kind, @f = found
      FROM Compat._JsonLocate(@json, @path);
    IF @f = 0 OR @f IS NULL RETURN;
    DECLARE @cend INT = @p + @l;

    -- Gốc vô hướng: một dòng key rỗng (giống OPENJSON).
    IF @k = N'S' OR @k = N'N' OR @k = N'T' OR @k = N'F' OR @k = N'U'
    BEGIN
        INSERT INTO @r([key], value, type)
            SELECT N'',
                CASE @k WHEN N'S' THEN Compat._JsonUnescape(SUBSTRING(@json, @p + 1, @l - 2))
                        WHEN N'U' THEN NULL ELSE SUBSTRING(@json, @p, @l) END,
                CASE @k WHEN N'U' THEN 0 WHEN N'S' THEN 1 WHEN N'N' THEN 2
                        WHEN N'T' THEN 3 WHEN N'F' THEN 3 ELSE 0 END;
        RETURN;
    END
    IF @k <> N'{' AND @k <> N'[' RETURN;

    DECLARE @tk TABLE(seq INT IDENTITY(1,1) PRIMARY KEY, kind NCHAR(1), pos INT, len INT, depth INT);
    INSERT INTO @tk(kind, pos, len, depth)
        SELECT kind, pos, len, depth FROM Compat._JsonTokens(@json) ORDER BY seq;
    IF EXISTS (SELECT 1 FROM @tk WHERE kind = N'!') RETURN;

    IF @k = N'{'
    BEGIN
        -- Mỗi thuộc tính cấp 1 là một dòng (bỏ qua giá trị chuỗi trùng tên như _JsonLocate).
        DECLARE @ps INT;
        SELECT TOP 1 @ps = seq FROM @tk
         WHERE pos > @p AND pos + len < @cend AND depth = @d + 1 AND kind = N'S' ORDER BY seq;
        WHILE @ps IS NOT NULL
        BEGIN
            DECLARE @ppos INT, @plen INT;
            SELECT @ppos = pos, @plen = len FROM @tk WHERE seq = @ps;
            DECLARE @colon INT;
            SELECT TOP 1 @colon = seq FROM @tk WHERE seq > @ps ORDER BY seq;
            DECLARE @colonKind NCHAR(1);
            SELECT @colonKind = kind FROM @tk WHERE seq = @colon;
            IF @colonKind = N':'
            BEGIN
                DECLARE @vpos INT, @vlen INT, @vk NCHAR(1);
                SELECT TOP 1 @vpos = pos, @vlen = len, @vk = kind
                  FROM @tk WHERE seq > @colon ORDER BY seq;
                INSERT INTO @r([key], value, type)
                    SELECT Compat._JsonUnescape(SUBSTRING(@json, @ppos + 1, @plen - 2)),
                        CASE @vk WHEN N'S' THEN Compat._JsonUnescape(SUBSTRING(@json, @vpos + 1, @vlen - 2))
                                 WHEN N'U' THEN NULL ELSE SUBSTRING(@json, @vpos, @vlen) END,
                        CASE @vk WHEN N'U' THEN 0 WHEN N'S' THEN 1 WHEN N'N' THEN 2
                                 WHEN N'T' THEN 3 WHEN N'F' THEN 3
                                 WHEN N'[' THEN 4 WHEN N'{' THEN 5 ELSE 0 END;
            END
            SELECT TOP 1 @ps = seq FROM @tk
             WHERE seq > @ps AND pos > @p AND pos + len < @cend AND depth = @d + 1 AND kind = N'S'
             ORDER BY seq;
            IF @@ROWCOUNT = 0 SET @ps = NULL;
        END
        RETURN;
    END

    -- Mảng: mỗi phần tử một dòng, key là chỉ số.
    DECLARE @idx INT = 0;
    DECLARE @es INT;
    SELECT TOP 1 @es = seq FROM @tk
     WHERE pos > @p AND pos + len < @cend AND depth = @d + 1
       AND kind IN (N'S', N'N', N'T', N'F', N'U', N'{', N'[')
     ORDER BY seq;
    WHILE @es IS NOT NULL
    BEGIN
        DECLARE @epos INT, @elen INT, @ek NCHAR(1);
        SELECT @epos = pos, @elen = len, @ek = kind FROM @tk WHERE seq = @es;
        INSERT INTO @r([key], value, type)
            SELECT CONVERT(NVARCHAR(4000), @idx),
                CASE @ek WHEN N'S' THEN Compat._JsonUnescape(SUBSTRING(@json, @epos + 1, @elen - 2))
                         WHEN N'U' THEN NULL ELSE SUBSTRING(@json, @epos, @elen) END,
                CASE @ek WHEN N'U' THEN 0 WHEN N'S' THEN 1 WHEN N'N' THEN 2
                         WHEN N'T' THEN 3 WHEN N'F' THEN 3
                         WHEN N'[' THEN 4 WHEN N'{' THEN 5 ELSE 0 END;
        SET @idx = @idx + 1;
        SELECT TOP 1 @es = seq FROM @tk
         WHERE seq > @es AND pos > @p AND pos + len < @cend AND depth = @d + 1
           AND kind IN (N'S', N'N', N'T', N'F', N'U', N'{', N'[')
         ORDER BY seq;
        IF @@ROWCOUNT = 0 SET @es = NULL;
    END
    RETURN;
END
GO

/* ----------------------------------------------------------------------------
   Compat.JsonEscape(@s): escape chuỗi theo kiểu JSON (đủ dùng cho mẫu
   FOR XML → JSON viết tay trên 2014).
   ---------------------------------------------------------------------------- */
CREATE FUNCTION Compat.JsonEscape(@s NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
AS
BEGIN
    IF @s IS NULL RETURN NULL;
    DECLARE @o NVARCHAR(MAX) = @s;
    SET @o = REPLACE(@o, N'\', N'\\');
    SET @o = REPLACE(@o, N'"', N'\"');
    SET @o = REPLACE(@o, NCHAR(8), N'\b');
    SET @o = REPLACE(@o, NCHAR(12), N'\f');
    SET @o = REPLACE(@o, NCHAR(10), N'\n');
    SET @o = REPLACE(@o, NCHAR(13), N'\r');
    SET @o = REPLACE(@o, NCHAR(9), N'\t');
    RETURN @o;
END
GO
