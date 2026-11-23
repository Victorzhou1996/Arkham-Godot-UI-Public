module Main where

import Cards.Discover.Exe
import Control.Exception (IOException, bracket, bracket_, try)
import Control.Monad (unless)
import Data.List (isInfixOf)
import GHC.IO.Encoding (getLocaleEncoding, setLocaleEncoding)
import System.Directory
import System.FilePath
import System.IO qualified as IO
import Prelude

check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS " ++ label)

main :: IO ()
main = do
  original <- getLocaleEncoding
  bracket_ (setLocaleEncoding IO.latin1) (setLocaleEncoding original) $ do
    temp <- getTemporaryDirectory
    bracket (IO.openTempFile temp "arkham-generator-utf8")
      (\(p, _) -> removeFile p) $ \(p, h) -> do
        IO.hClose h
        let source = "-- 中文 “智能引号” — • ☾\nfoo :: CardDef\nbar\n  :: CardDef\nécho :: CardDef\n"
        writeGeneratedUtf8 p source
        readSourceUtf8 p >>= check "generator UTF8 roundtrip under Latin1 locale" . (== source)
        let Just spec = homebrewSpec (CardDefsRegistry "Asset")
        entries <- readHomebrewEntries spec (Module "Fixture.Assets" p)
        check "generator Unicode comments and wrapped signatures" (map heBuilder entries == ["foo", "bar", "écho"])
        let generated = renderHomebrewContentFile spec (Module "Fixture.Registry" p) entries
        writeGeneratedUtf8 p generated
        readSourceUtf8 p >>= check "generator UTF8 identifier output retained" . isInfixOf "Card1.écho"
        IO.withBinaryFile p IO.WriteMode $ \bad -> IO.hPutChar bad '\255'
        invalid <- try @IOException (readSourceUtf8 p)
        check "generator rejects malformed UTF8" (either (const True) (const False) invalid)
        let cases =
              [ ("Act/Acts.hs", "Cards", CardBuilders "Act", "banishHim")
              , ("Agenda/CardDefEntries.hs", "CardDefs", CardDefsRegistry "Agenda", "justiceXI")
              , ("Homebrew/CircusExMortis/CardDefEntries.hs", "CardDefs", HomebrewCardDefs, "carrieDykstra")
              ]
        mapM_ (\(entry, dir, mode, expected) -> do
          discoverCardsWith (Source ("arkham-core/library/Arkham" </> entry)) (Destination p) dir Nothing mode
          contents <- readSourceUtf8 p
          check ("generator actual registry " ++ expected) (isInfixOf ("." ++ expected) contents)) cases
  putStrLn "Generator encoding specs completed (not core rule coverage)."
