module Arkham.Native.StrictJSON (validateJsonProperties) where

import Arkham.Prelude
import Data.ByteString qualified as BS
import Data.Set qualified as Set

-- Must precede Aeson's object decoding. Escaped names are decoded by Aeson;
-- exact duplicates at ANY nesting level are rejected before Map construction.
-- No number is parsed/re-encoded here, so Int64 seeds/counts remain exact.
validateJsonProperties :: BS.ByteString -> Either Text ()
validateJsonProperties bytes = do
  end <- value 0 $ spaces 0
  unless (spaces end == BS.length bytes) bad
 where
  bad = Left "Invalid JSON structure or duplicate property"
  size = BS.length bytes
  atByte index = if index < size then Just (BS.index bytes index) else Nothing
  spaces index = case atByte index of
    Just c | c `elem` [9,10,13,32] -> spaces (index+1)
    _ -> index
  string index = go (index+1)
   where
    go cursor = case atByte cursor of
      Just 34 -> Right $ cursor+1
      Just 92 | cursor+1 < size -> go (cursor+2)
      Just c | c >= 32 -> go (cursor+1)
      _ -> bad
  value depth index
    | depth > (128 :: Int) = bad
    | otherwise = case atByte index of
        Just 123 -> objectValue (depth+1) Set.empty $ spaces $ index+1
        Just 91 -> arrayValue (depth+1) $ spaces $ index+1
        Just 34 -> string index
        Just c | c `notElem` [44,58,93,125] -> primitive index index
        _ -> bad
  primitive start cursor = case atByte cursor of
    Just c | c `notElem` [9,10,13,32,44,58,91,93,123,125,34] -> primitive start (cursor+1)
    _ | cursor > start -> Right cursor
    _ -> bad
  objectValue depth names index = case atByte index of
    Just 125 | Set.null names -> Right $ index+1
    Just 34 -> do
      end <- string index
      name <- case eitherDecodeStrict' (BS.take (end-index) $ BS.drop index bytes) of
        Right text -> Right (text :: Text)
        Left _ -> bad
      when (Set.member name names) bad
      let colon = spaces end
      unless (atByte colon == Just 58) bad
      after <- value depth $ spaces $ colon+1
      let next = spaces after
      case atByte next of
        Just 125 -> Right $ next+1
        Just 44 -> objectValue depth (Set.insert name names) $ spaces $ next+1
        _ -> bad
    _ -> bad
  arrayValue depth index = case atByte index of
    Just 93 -> Right $ index+1
    _ -> arrayItem depth index
  arrayItem depth index = do
    after <- value depth index
    let next = spaces after
    case atByte next of
      Just 93 -> Right $ next+1
      Just 44 -> arrayItem depth $ spaces $ next+1
      _ -> bad
