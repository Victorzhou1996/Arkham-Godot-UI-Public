-- Mechanical pure extraction from the immutable Oct15 baseline:
-- Api/Arkham/Epic.hs:64-118 and
-- Api/Handler/Arkham/Games/Shared.hs:1124-1131.
-- No SQL locking/persistence/broadcast logic belongs in this module.
module Arkham.Native.Epic.Shared where

import Arkham.Prelude
import Arkham.Card.CardCode (CardCode (..))
import Arkham.Epic.Types
import Arkham.Message (Message (ScenarioCountSet))
import Arkham.ScenarioLogKey (ScenarioCountKey (EpicShared))
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set

applyBoundedDelta :: SharedEventState -> SharedDelta -> (SharedEventState, SharedDelta)
applyBoundedDelta s d =
  let current = sharedCounter d.sharedDeltaKey s
      proposed = current + d.sharedDeltaAmount
      bounded = case d.sharedDeltaKey of
        Countermeasures -> max 0 proposed
        SharedEnemyHealth (CardCode "85037") -> max 0 $ min (15 * sharedTotalInvestigators s) proposed
        SharedEnemyHealth _ -> max 0 proposed
        SharedActProgress _ -> max 0 proposed
        AdvanceRequested _ -> max 0 proposed
        ActAdvanceGen _ -> max 0 proposed
        ActContribution _ _ -> max 0 proposed
        ActSpend _ _ -> max 0 proposed
        _ -> proposed
      effective = d {sharedDeltaAmount = bounded - current}
   in (applyDelta effective s, effective)

applyBoundedDeltas :: SharedEventState -> [SharedDelta] -> (SharedEventState, [SharedDelta])
applyBoundedDeltas = go []
 where
  go acc s [] = (s, reverse acc)
  go acc s (d : ds)
    | sharedDeltaId d `Set.member` sharedAppliedDeltas s = go acc s ds
    | otherwise =
        let (s', effective) = applyBoundedDelta s d
         in go (effective : acc) s' ds

armActAdvanceGates :: SharedEventState -> SharedEventState
armActAdvanceGates s = foldl' arm s (actProgressStages s)
 where
  arm st stage
    | sharedCounter (AdvanceRequested stage) st <= 0 = st
    | otherwise =
        let pool = sharedCounter (SharedActProgress stage) st
            threshold = 2 * sharedTotalInvestigators st
            withGate = if threshold > 0 && pool >= threshold
              then setSharedCounter (AwaitingOrganizer stage) 1 st else st
         in setSharedCounter (AdvanceRequested stage) 0 withGate

bumpSharedRevision :: SharedEventState -> SharedEventState -> SharedEventState
bumpSharedRevision old new
  | old == new = old
  | otherwise = new {sharedVersion = sharedVersion old + 1}

epicSyncMessages :: GroupOrdinal -> SharedEventState -> [Message]
epicSyncMessages (GroupOrdinal ordinal) shared =
  ScenarioCountSet (EpicShared totalInvestigatorsKey) (sharedTotalInvestigators shared)
    : ScenarioCountSet (EpicShared groupOrdinalKey) ordinal
    : [ScenarioCountSet (EpicShared k) v | (k,v) <- Map.toList (sharedCounters shared)]
